using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

/*
 * ShurikenDensityProbe (editor-only diagnostic, STAGE 6 FOLLOW-UP)
 *
 * READ-ONLY Play-Mode recorder for a CONVERTED (Shuriken) ParticleSystem -
 * the exact counterpart of LegacyParticleMotionRecorder, which measures the
 * ORIGINAL legacy emitters. Purpose: quantify the emission-density and
 * expansion discrepancies reported in the in-engine A/B by capturing, over a
 * user-configured duration (>= 20 s recommended), for the V2 [PSV2] system:
 *
 *   - live particle counts over time,
 *   - per-particle birth events (Particle.randomSeed identity, doc-verified
 *     uint in 2017.4) -> offline estimated birth-rate series and lifetime
 *     distribution of NEWLY SPAWNED particles,
 *   - particle velocity/size/startSize/startLifetime distributions
 *     (raw simulation-space values, recorded verbatim, never transformed),
 *   - distance-from-emitter vs age (raw column is the scale-1 emitter-local
 *     frame; world columns are transported by the transform exactly like the
 *     legacy recorder builds them: MultiplyPoint3x4 for positions, that is
 *     rotation + translation + SCALE, and TransformDirection for velocities,
 *     rotation only - lossyScale is recorded per sample for verification, so
 *     both systems can be compared in the same coordinate space offline),
 *   - the LIVE module state of the system every sample: Emission module
 *     enabled flag + rate values, Velocity over Lifetime enabled flag + axis
 *     values, startSpeed, maxParticles, and the runtime toggle state of any
 *     attached V2 experiment components (velocity rotation / tangent emitter
 *     / orbit / velocity multiplier). This is the direct runtime evidence for
 *     the two follow-up questions:
 *       * "did Stage 4 leave Shuriken's automatic emission disabled?"
 *         (recorded emissionEnabled column answers it for the record),
 *       * "does Stage 6's VOLT takeover affect initial velocities?"
 *         (voltEnabled column + the raw velocity columns of fresh spawns
 *         (isNew=1) answer it empirically, per spawn).
 *
 * READ-ONLY GUARANTEE (doctrine, mirrors the Stage-5 recorder):
 *
 *   - This window attaches NOTHING to the scene and creates no components.
 *     It is an EditorWindow that only CALLS GETTERS (GetParticles fills a
 *     buffer owned by the window; SetParticles is never called; no module is
 *     enabled/disabled; no SerializedObject is written back).
 *   - The recorded system renders exactly as it would without the probe.
 *
 * MEASUREMENT SEMANTICS:
 *
 *   - Sample clock: Time.time (simulation clock), same clock the legacy
 *     recorder uses, so two recorders started in the SAME Play session are
 *     directly aligned on time_s for the A/B.
 *   - isNew (0/1): 1 for the first sighting of an unseen randomSeed AFTER
 *     recording started. Seeds alive at Start are pre-seeded into the seen
 *     set and NEVER reported isNew (they predate the recording; birth-time
 *     analysis must therefore ignore the first ~max lifetime seconds - 6 s
 *     for the reference system - or subtract the stale rows by isNew=0).
 *   - birthsThisSample / cumulativeBirths in the system CSV make the birth
 *     rate directly integrable (offline rates/bins need no seed bookkeeping).
 *   - CSV column semantics mirror LegacyParticleMotionRecorder wherever the
 *     same quantity exists (pos/vel raw, world transport, remaining/starting
 *     lifetime, size), so analysis scripts can be reused almost as-is.
 */

public class ShurikenDensityProbe : EditorWindow
{
	const string kToolTitle = "Shuriken Density Probe";

	[MenuItem("Tools/Particle System/Diagnostics/Open Shuriken Density Probe", false, 401)]
	static void OpenWindow()
	{
		ShurikenDensityProbe window = GetWindow<ShurikenDensityProbe>(kToolTitle);
		window.minSize = new Vector2(420f, 300f);
	}

	[Header("Capture target")]
	[SerializeField]
	ParticleSystem targetSystem;

	[Header("Sampling")]
	[Tooltip("Seconds between samples (simulation time). 0.1 s = 10 samples/s.")]
	public float sampleInterval = 0.1f;

	[Tooltip("Auto-stop after this many seconds of recording (20+ s recommended for birth-rate statistics).")]
	public float recordDuration = 20f;

	bool recording;
	double recordStartTime; // Time.time at Start (simulation clock)
	int sampleIndex;
	double lastSampleTime;
	string sessionStamp;

	readonly List<string> particleRows = new List<string>();
	readonly List<string> systemRows = new List<string>();

	// Birth identity state: fresh-spawn detection by randomSeed (2017.4 uint,
	// doc-verified). Seeds alive at Start do NOT count as births.
	HashSet<uint> seenSeeds = new HashSet<uint>();
	int cumulativeBirths;

	// ---------------------------------------------------------------------
	// Window UI
	// ---------------------------------------------------------------------

	void OnGUI()
	{
		EditorGUILayout.HelpBox(
			"READ-ONLY Stage-6 follow-up diagnostic. Records the live particles of a CONVERTED (Shuriken) ParticleSystem during Play Mode.\n" +
			"Nothing is attached to the scene and nothing is written back (no SetParticles, no module changes).\n" +
			"CSV output goes to <project>/Logs/ when the recording stops (particles + system-state files).\n" +
			"For a same-clock A/B, run LegacyParticleMotionRecorder (legacy Cloud_bad) in the SAME Play session.",
			MessageType.Info);

		EditorGUI.BeginDisabledGroup(recording);
		targetSystem = (ParticleSystem)EditorGUILayout.ObjectField(
			"Target ParticleSystem", targetSystem, typeof(ParticleSystem), true);

		sampleInterval = EditorGUILayout.FloatField("Sample interval (s)", sampleInterval);
		recordDuration = EditorGUILayout.FloatField("Record duration (s)", recordDuration);
		EditorGUI.EndDisabledGroup();

		EditorGUILayout.Space();

		if (!Application.isPlaying)
			EditorGUILayout.HelpBox("Enter Play Mode, press Start. Recording is Play-Mode-only.", MessageType.Warning);

		if (!recording)
		{
			EditorGUI.BeginDisabledGroup(!Application.isPlaying || targetSystem == null);
			if (GUILayout.Button("Start Recording", GUILayout.Height(30f)))
				StartRecording();
			EditorGUI.EndDisabledGroup();
		}
		else
		{
			float elapsed = (float)(Now() - recordStartTime);
			EditorGUILayout.LabelField("Recording...", string.Format(
				CultureInfo.InvariantCulture,
				"{0:F2} s / {1:F2} s  -  {2} samples  -  {3} particles in last sample  -  {4} births",
				elapsed, recordDuration, sampleIndex, LastParticleCount(), cumulativeBirths));

			bool stoppedByDuration = elapsed >= recordDuration;
			if (stoppedByDuration)
				EditorGUILayout.HelpBox("Duration reached - stopping and writing CSV files...", MessageType.Warning);

			if (GUILayout.Button("Stop Recording", GUILayout.Height(30f)))
				StopRecording(stoppedByDuration ? "duration" : "manual");
		}

		EditorGUILayout.Space();
		EditorGUILayout.HelpBox(
			"WORKFLOW (V2 [PSV2] Cloud_bad, 20 s):\n" +
			"1. In Play Mode, select the legacy Cloud_bad object or its [PSV2] child; 'Use selected system' auto-picks the holder.\n" +
			"2. Leave defaults (0.1 s, 20 s) or set 0.05 s for finer birth-time resolution.\n" +
			"3. Press Start Recording; do not toggle experiments mid-recording unless that IS the test.\n" +
			"4. Files: particles CSV (per-particle rows, randomSeed identity, isNew flag) + system CSV (counts, module state, experiment toggles).\n" +
			"5. Birth/lifetime statistics: ignore the first ~6 s (particles pre-alive at Start are isNew=0 by design) or use lifetime 3..6 for the exact window.",
			MessageType.None);

		if (GUILayout.Button("Use selected system"))
		{
			GameObject selected = Selection.activeGameObject;
			if (selected != null)
			{
				ParticleSystem fromSelection = selected.GetComponent<ParticleSystem>();
				if (fromSelection == null && selected.transform.parent != null)
					fromSelection = selected.transform.parent.GetComponent<ParticleSystem>(); // legacy GO -> its [PSV2] holder
				if (fromSelection != null)
					targetSystem = fromSelection;
				else
					ShowNotification(new GUIContent(selected.name + " has no ParticleSystem (directly or on its parent)"));
			}
		}
	}

	int LastParticleCount()
	{
		if (!recording || targetSystem == null)
			return 0;
		return targetSystem.particleCount;
	}

	// ---------------------------------------------------------------------
	// Recording loop (EditorApplication.update, simulation-time-driven)
	// ---------------------------------------------------------------------

	void OnEnable()
	{
		EditorApplication.update += OnEditorUpdate;
	}

	void OnDisable()
	{
		EditorApplication.update -= OnEditorUpdate;
		if (recording)
			StopRecording("window closed");
	}

	void OnEditorUpdate()
	{
		if (!recording)
			return;

		// Play mode ended without a manual stop: flush what was captured.
		if (!Application.isPlaying)
		{
			StopRecording("play mode exited");
			return;
		}

		double now = Now();
		if (now - recordStartTime >= recordDuration)
		{
			StopRecording("duration");
			return;
		}

		// Sample on SIMULATION time (Time.time), same clock as the legacy recorder.
		if (now - lastSampleTime < sampleInterval - 1e-6)
			return;

		CaptureSample(now);
	}

	/// <summary>Simulation clock (invalid outside Play - callers keep recording Play-only).</summary>
	double Now()
	{
		return Application.isPlaying ? (double)Time.time : (double)Time.realtimeSinceStartup;
	}

	void StartRecording()
	{
		if (targetSystem == null)
			return;

		particleRows.Clear();
		systemRows.Clear();
		seenSeeds.Clear();
		cumulativeBirths = 0;
		sampleIndex = 0;
		lastSampleTime = Now() - sampleInterval; // sample immediately
		recordStartTime = Now();
		sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

		// Pre-seed the seen set with the currently alive particles so the
		// recording's birth statistics only count particles born AFTER Start.
		ParticleSystem.Particle[] initial = AllocateBuffer();
		int initialCount = targetSystem.GetParticles(initial);
		for (int i = 0; i < initialCount; i++)
			seenSeeds.Add(initial[i].randomSeed);

		recording = true;

		ParticleSystem.VelocityOverLifetimeModule volt = targetSystem.velocityOverLifetime;
		ParticleSystem.EmissionModule emission = targetSystem.emission;
		Debug.Log(kToolTitle + ": recording " + GetTransformPath(targetSystem.transform)
			+ " (simSpace=" + targetSystem.main.simulationSpace
			+ ", interval=" + sampleInterval.ToString("R", CultureInfo.InvariantCulture)
			+ " s, duration=" + recordDuration.ToString("R", CultureInfo.InvariantCulture)
			+ " s, pre-alive seeds=" + initialCount
			+ ", emission.enabled=" + emission.enabled
			+ ", volt.enabled=" + volt.enabled + ")");
	}

	ParticleSystem.Particle[] AllocateBuffer()
	{
		int wanted = Mathf.Max(targetSystem.particleCount, 1);
		return new ParticleSystem.Particle[wanted];
	}

	void CaptureSample(double now)
	{
		ParticleSystem ps = targetSystem;
		Transform t = ps.transform;

		// Per-sample transform state (same convention as the legacy recorder:
		// full localToWorld for positions, TransformDirection for velocities,
		// lossyScale recorded for offline space verification).
		Matrix4x4 l2w = t.localToWorldMatrix;
		Vector3 worldPos = t.position;
		Vector3 lossy = t.lossyScale;
		string wallClock = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

		ParticleSystem.MainModule main = ps.main;
		ParticleSystem.EmissionModule emission = ps.emission;
		ParticleSystem.VelocityOverLifetimeModule volt = ps.velocityOverLifetime;

		// THE READ: GetParticles fills OUR buffer; never written back.
		ParticleSystem.Particle[] buffer = AllocateBuffer();
		int count = ps.GetParticles(buffer);

		double dtThis = now - lastSampleTime; // > 0 (except the immediate first sample)
		double time = now;
		lastSampleTime = now;

		string systemName = SanitizeName(ps.gameObject.name);
		int systemID = ps.GetInstanceID();
		string simSpaceName = main.simulationSpace.ToString();

		// ---- Particle pass first: birth bookkeeping (randomSeed identity) and
		// per-particle rows. birthsThisSample feeds the system row below.
		int birthsThisSample = 0;

		for (int i = 0; i < count; i++)
		{
			ParticleSystem.Particle p = buffer[i];

			bool isNew = !seenSeeds.Contains(p.randomSeed);
			if (isNew)
			{
				seenSeeds.Add(p.randomSeed);
				birthsThisSample++;
				cumulativeBirths++;
			}

			Vector3 worldPosP = p.position;
			Vector3 worldVelP = p.velocity;
			if (main.simulationSpace != ParticleSystemSimulationSpace.World)
			{
				worldPosP = l2w.MultiplyPoint3x4(p.position);
				worldVelP = t.TransformDirection(p.velocity);
			}

			particleRows.Add(JoinCsv(
				sampleIndex.ToString(CultureInfo.InvariantCulture),
				time.ToString("R", CultureInfo.InvariantCulture),
				wallClock,
				systemName,
				systemID.ToString(CultureInfo.InvariantCulture),
				simSpaceName,
				p.randomSeed.ToString(CultureInfo.InvariantCulture),
				isNew ? "1" : "0",
				i.ToString(CultureInfo.InvariantCulture),
				FNum(p.position.x), FNum(p.position.y), FNum(p.position.z),
				FNum(p.velocity.x), FNum(p.velocity.y), FNum(p.velocity.z),
				FNum(worldPosP.x), FNum(worldPosP.y), FNum(worldPosP.z),
				FNum(worldVelP.x), FNum(worldVelP.y), FNum(worldVelP.z),
				FNum(p.remainingLifetime), FNum(p.startLifetime),
				FNum(p.startLifetime - p.remainingLifetime),
				FNum(p.startSize), FNum(p.size),
				FNum(p.rotation), FNum(p.angularVelocity),
				FNum(p.color.r), FNum(p.color.g), FNum(p.color.b), FNum(p.color.a)));
		}

		// Retire seeds that left the alive set so the set cannot grow unboundedly.
		if (seenSeeds.Count > count)
		{
			HashSet<uint> alive = new HashSet<uint>();
			for (int i = 0; i < count; i++)
				alive.Add(buffer[i].randomSeed);
			seenSeeds.RemoveWhere(delegate (uint s) { return !alive.Contains(s); });
		}

		// ---- System-state row: counts + live module state + experiment toggles.
		List<string> sys = new List<string>(64);
		sys.Add(sampleIndex.ToString(CultureInfo.InvariantCulture));
		sys.Add(time.ToString("R", CultureInfo.InvariantCulture));
		sys.Add(wallClock);
		sys.Add(systemName);
		sys.Add(systemID.ToString(CultureInfo.InvariantCulture));
		sys.Add(simSpaceName);
		sys.Add(ps.IsAlive() ? "1" : "0");
		sys.Add(ps.particleCount.ToString(CultureInfo.InvariantCulture));
		sys.Add(count.ToString(CultureInfo.InvariantCulture));
		sys.Add(FNum(dtThis));
		sys.Add(birthsThisSample.ToString(CultureInfo.InvariantCulture));
		sys.Add(cumulativeBirths.ToString(CultureInfo.InvariantCulture));

		sys.Add(Bool01(emission.enabled));
		AppendCurve(sys, emission.rateOverTime);
		sys.Add(ps.maxParticles.ToString(CultureInfo.InvariantCulture));

		sys.Add(Bool01(volt.enabled));
		AppendCurve(sys, volt.x);
		AppendCurve(sys, volt.y);
		AppendCurve(sys, volt.z);
		AppendCurve(sys, volt.speedModifier);
		AppendCurve(sys, main.startSpeed);

		sys.Add(FNum(worldPos.x));
		sys.Add(FNum(worldPos.y));
		sys.Add(FNum(worldPos.z));
		sys.Add(FNum(lossy.x));
		sys.Add(FNum(lossy.y));
		sys.Add(FNum(lossy.z));

		// Experiment toggles ACTUALLY RUNNING at this instant (-1 = component absent).
		AppendExperimentFlags(sys);

		systemRows.Add(JoinCsv(sys.ToArray()));

		sampleIndex++;
	}

	// ---------------------------------------------------------------------
	// Small helpers
	// ---------------------------------------------------------------------

	static string FNum(float v)
	{
		return v.ToString("R", CultureInfo.InvariantCulture);
	}

	static string FNum(double v)
	{
		return v.ToString("R", CultureInfo.InvariantCulture);
	}

	static string Bool01(bool b)
	{
		return b ? "1" : "0";
	}

	/// <summary>Mode + constant + constantMin + constantMax of a MinMaxCurve (2017.4 API: no scalar property; copy-of-value, not by ref).</summary>
	static void AppendCurve(List<string> cells, ParticleSystem.MinMaxCurve curve)
	{
		cells.Add(((int)curve.mode).ToString(CultureInfo.InvariantCulture));
		cells.Add(FNum(curve.constant));
		cells.Add(FNum(curve.constantMin));
		cells.Add(FNum(curve.constantMax));
	}

	/// <summary>
	/// Runtime toggle state of attached V2 experiment components at this instant.
	/// -1 cells = component absent. Read via SerializedObject (read-only) so
	/// serialized-only fields (velocity-rotation state machine) remain readable
	/// without writing anything.
	/// </summary>
	void AppendExperimentFlags(List<string> cells)
	{
		// Velocity rotation experiment (Stage 6)
		LegacyParticleVelocityRotation rotExp = targetSystem.GetComponent<LegacyParticleVelocityRotation>();
		if (rotExp == null)
			cells.Add("-1");
		else
		{
			SerializedObject so = new SerializedObject(rotExp);
			SerializedProperty on = so.FindProperty("enableVelocityRotation");
			SerializedProperty running = so.FindProperty("experimentRunning");
			SerializedProperty captured = so.FindProperty("stateCaptured");
			cells.Add(string.Join(";", new string[]
			{
				on != null ? (on.boolValue ? "1" : "0") : "?",
				running != null ? (running.boolValue ? "1" : "0") : "?",
				captured != null ? (captured.boolValue ? "1" : "0") : "?"
			}));
		}

		// Tangent emitter experiment (Stage 4)
		LegacyParticleTangentEmitter tanExp = targetSystem.GetComponent<LegacyParticleTangentEmitter>();
		cells.Add(tanExp != null ? Bool01(tanExp.enableTangentEmission) : "-1");

		// Orbit experiment (Stage 2)
		LegacyParticleOrbit orbit = targetSystem.GetComponent<LegacyParticleOrbit>();
		cells.Add(orbit != null ? Bool01(orbit.enableOrbitalSimulation) : "-1");

		// Velocity multiplier (Stage 3b presence)
		cells.Add(targetSystem.GetComponent<LegacyParticleVelocityMultiplier>() != null ? "1" : "0");
	}

	// ---------------------------------------------------------------------
	// CSV writing (project Logs/ convention, like the Stage-5 recorder)
	// ---------------------------------------------------------------------

	void StopRecording(string reason)
	{
		recording = false;

		string stamp = sessionStamp != null ? sessionStamp : DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
		string tag = targetSystem != null ? SanitizeName(targetSystem.gameObject.name) : "unknown";

		string particlesPath = WriteCsv("ShurikenDensity_" + tag + "_particles_" + stamp, BuildParticlesCsv());
		string systemPath = WriteCsv("ShurikenDensity_" + tag + "_system_" + stamp, BuildSystemCsv());

		string message = kToolTitle + ": stopped (" + reason + ") - " + sampleIndex + " samples, " + cumulativeBirths + " births.\n  particles: " + particlesPath + "\n  system  : " + systemPath;
		Debug.Log(message);
		EditorUtility.DisplayDialog(kToolTitle, message, "OK");
	}

	string BuildParticlesCsv()
	{
		List<string> lines = new List<string>();
		lines.Add(JoinCsv(
			"sampleIndex", "time_s", "wallClock", "systemName", "systemInstanceID", "simSpace",
			"randomSeed", "isNew", "particleIndex",
			"posX", "posY", "posZ", "velX", "velY", "velZ",
			"worldPosX", "worldPosY", "worldPosZ", "worldVelX", "worldVelY", "worldVelZ",
			"remainingLifetime", "startLifetime", "age",
			"startSize", "size", "rotation", "angularVelocity",
			"colorR", "colorG", "colorB", "colorA"));
		lines.AddRange(particleRows);
		return string.Join("\n", lines.ToArray()) + "\n";
	}

	string BuildSystemCsv()
	{
		List<string> lines = new List<string>();
		lines.Add(JoinCsv(
			"sampleIndex", "time_s", "wallClock", "systemName", "systemInstanceID", "simSpace",
			"isAlive", "particleCount", "liveCount", "dtSinceLastSample", "birthsThisSample", "cumulativeBirths",
			"emissionEnabled",
			"rateMode", "rateConstant", "rateConstantMin", "rateConstantMax",
			"maxParticles",
			"voltEnabled",
			"voltModeX", "voltConstX", "voltMinX", "voltMaxX",
			"voltModeY", "voltConstY", "voltMinY", "voltMaxY",
			"voltModeZ", "voltConstZ", "voltMinZ", "voltMaxZ",
			"speedMode", "speedConstant", "speedConstantMin", "speedConstantMax",
			"startSpeedMode", "startSpeedConstant", "startSpeedConstantMin", "startSpeedConstantMax",
			"systemWorldPosX", "systemWorldPosY", "systemWorldPosZ",
			"lossyScaleX", "lossyScaleY", "lossyScaleZ",
			"rotExpToggle;running;captured", "tangentExpToggle", "orbitExpToggle", "multiplierPresent"));
		lines.AddRange(systemRows);
		return string.Join("\n", lines.ToArray()) + "\n";
	}

	static string WriteCsv(string baseName, string content)
	{
		try
		{
			string logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
			if (!Directory.Exists(logsDir))
				Directory.CreateDirectory(logsDir);

			string path = Path.Combine(logsDir, baseName + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
			File.WriteAllText(path, content);
			return path;
		}
		catch (IOException ex)
		{
			return "<write failed: " + ex.Message + ">";
		}
		catch (System.UnauthorizedAccessException ex)
		{
			return "<write failed: " + ex.Message + ">";
		}
	}

	static string JoinCsv(params string[] cells)
	{
		for (int i = 0; i < cells.Length; i++)
		{
			if (cells[i] != null && (cells[i].IndexOf(',') >= 0 || cells[i].IndexOf('"') >= 0 || cells[i].IndexOf('\n') >= 0))
				cells[i] = "\"" + cells[i].Replace("\"", "\"\"") + "\"";
		}
		return string.Join(",", cells);
	}

	static string SanitizeName(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unnamed";
		return name.Replace(',', '_').Replace('"', '_').Replace('\n', '_').Replace('\r', '_');
	}

	static string GetTransformPath(Transform t)
	{
		string path = t.name;
		while (t.parent != null)
		{
			t = t.parent;
			path = t.name + "/" + path;
		}
		return path;
	}
}
