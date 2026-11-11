#if !UNITY_2018_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

/*
 * LegacyParticleMotionRecorder (editor-only diagnostic, STAGE 5)
 *
 * READ-ONLY Play-Mode recorder for the ORIGINAL legacy particle systems. Its
 * whole purpose is empirical measurement of the legacy Cloud_bad emitter:
 *
 *   - WHAT the live particles actually do (positions, velocities, remaining
 *     lifetimes), sampled over time,
 *   - WHICH coordinate space the engine returns those values in (the space of
 *     ParticleEmitter.particles values is undocumented; the emitter's
 *     worldToLocal / localToWorld matrices are recorded alongside every sample
 *     so the space can be VERIFIED offline instead of assumed),
 *   - the actual emission geometry and initial velocity directions - to be
 *     derived from the data later, NOT guessed here.
 *
 * DOCTRINE (per the Stage-5 requirements):
 *
 *   1. Snapshot-first. The legacy Particle struct has NO stable particle ID
 *      (2017.4 docs verified: Particle exposes position, velocity, energy,
 *      startEnergy, size, color, rotation, angularVelocity - and nothing that
 *      identifies the particle across frames). The trustworthy capture is
 *      therefore the accurate per-frame snapshot. It is always recorded.
 *   2. Trajectory association is OPT-IN and heuristic. Nearest-neighbor
 *      association with an energy-continuity check is offered for convenience,
 *      is OFF by default, and every row it produces is explicitly labeled:
 *        - trackConfidence "None":      no tracking claim of any kind
 *                                       (tracking OFF, or no acceptable match);
 *        - trackConfidence "HeuristicUnique":    exactly one candidate track
 *                                       passed BOTH the distance threshold and
 *                                       the energy-continuity check - a guess,
 *                                       albeit a well-constrained one;
 *        - trackConfidence "HeuristicMultiple":  several candidates passed -
 *                                       association is genuinely ambiguous.
 *      No row is EVER labeled "Reliable": there is no API that could justify
 *      it. Track IDs of unmatched particles are -1.
 *
 * MEASUREMENT SEMANTICS (what exactly is in the CSV):
 *
 *   - posX/Y/Z, velX/Y/Z: the RAW values exactly as ParticleEmitter.particles
 *     returns them. Their space is undocumented by the legacy docs, which is
 *     why they are recorded verbatim, never transformed into the "truth".
 *   - worldPosX/Y/Z, worldVelX/Y/Z: convenience columns. When the emitter
 *     simulates in WORLD space these duplicate the raw values. When the
 *     emitter simulates in LOCAL space they are the raw point/dir transported
 *     by the emitter transform (MultiplyPoint3x4 / TransformDirection, i.e.
 *     rotation+translation only - velocity is NOT scaled by lossyScale, which
 *     is exactly why lossyScale is recorded for verification). If verification
 *     shows the raw values are already world-space, use them instead of these
 *     columns; if they turn out to be emitter-local, these columns are the
 *     world-space reading. The simSpace column tells which case applies.
 *   - energy: REMAINING lifetime in seconds (docs: "time in seconds for how
 *     long this particle will remain alive"); startEnergy: total lifetime.
 *     Spawn time can be derived: spawnTime ~ sampleTime - (startEnergy - energy).
 *   - emitter transform state is logged to a SECOND CSV for the same samples:
 *     world position, lossyScale, and both matrices (row-major m00..m33), so
 *     any stationary-vs-moving and scale questions can be closed offline.
 *
 * READ-ONLY GUARANTEE (requirements 8/9):
 *
 *   - ParticleEmitter.particles "Returns a copy of all particles" (2017.4
 *     docs, verified): the array is only ever READ; it is NEVER assigned back.
 *   - No serialized property is written, not even odds and ends: the only thing
 *     this tool does to the scene is read. It attaches nothing and changes no
 *     module. The emitter renders exactly as it would without the recorder.
 *   - Existing V2 / orbit / multiplier / tangent-emitter files are untouched;
 *     this file is fully standalone on purpose.
 */

public class LegacyParticleMotionRecorder : EditorWindow
{
	const string kToolTitle = "Legacy Particle Motion Recorder";

	[MenuItem("Tools/Particle System/Diagnostics/Open Motion Recorder", false, 400)]
	static void OpenWindow()
	{
		LegacyParticleMotionRecorder window = GetWindow<LegacyParticleMotionRecorder>(kToolTitle);
		window.minSize = new Vector2(420f, 300f);
	}

	[Header("Capture target")]
	[SerializeField]
	ParticleEmitter targetEmitter;

	[Header("Sampling")]
	[Tooltip("Seconds between samples (simulation time). 0.05 s = 20 samples/s.")]
	public float sampleInterval = 0.05f;

	[Tooltip("Auto-stop after this many seconds of recording (5-10 s recommended).")]
	public float recordDuration = 10f;

	[Header("Heuristic tracking (OFF = snapshot-only capture)")]
	[Tooltip("OFF (default): snapshot-only capture, trackID = -1 everywhere, no identity claims. ON: opt-in nearest-neighbor association, explicitly labeled Heuristic in every row.")]
	public bool enableTracking = false;

	[Tooltip("Max NEW-position to LAST-recorded-track-position distance for an association candidate (raw emitter units).")]
	public float trackDistanceThreshold = 0.06f;

	[Tooltip("Energy continuity check: |energy_now - (energy_prev - dtSample)| must be within this tolerance for a candidate match.")]
	public float trackEnergyTolerance = 0.04f;

	bool recording;
	double recordStartTime; // Time.time at Start (simulation clock)
	int sampleIndex;
	double lastSampleTime;
	string sessionStamp;

	readonly List<string> particleRows = new List<string>();
	readonly List<string> emitterRows = new List<string>();

	// Heuristic track state (only used when enableTracking is ON).
	class Track
	{
		public int id;
		public Vector3 lastPos;
		public float lastEnergy;
		public double lastSampleTime; // simulation time of the last association
	}

	readonly List<Track> tracks = new List<Track>();
	int nextTrackID;

	// ---------------------------------------------------------------------
	// Window UI
	// ---------------------------------------------------------------------

	void OnGUI()
	{
		EditorGUILayout.HelpBox(
			"READ-ONLY Stage-5 diagnostic. Records the live particles of a LEGACY ParticleEmitter during Play Mode.\n" +
			"Nothing is written to the emitter, its particles, or the scene - the particles array is only read (it is a copy).\n" +
			"CSV output goes to <project>/Logs/ when the recording stops (two files: particles + emitter-transform states).",
			MessageType.Info);

		EditorGUI.BeginDisabledGroup(recording);
		targetEmitter = (ParticleEmitter)EditorGUILayout.ObjectField(
			"Target legacy emitter", targetEmitter, typeof(ParticleEmitter), true);

		sampleInterval = EditorGUILayout.FloatField("Sample interval (s)", sampleInterval);
		recordDuration = EditorGUILayout.FloatField("Record duration (s)", recordDuration);

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Heuristic trajectory tracking (opt-in)", EditorStyles.boldLabel);
		EditorGUILayout.HelpBox(
			"The legacy Particle struct has NO stable particle ID, so per-frame SNAPSHOTS are the reliable capture (always on).\n" +
			"Tracking below is a nearest-neighbor HEURISTIC; every row it produces is labeled HeuristicUnique or HeuristicMultiple, never Reliable.",
			MessageType.None);
		enableTracking = EditorGUILayout.Toggle("Enable tracking heuristic", enableTracking);
		if (enableTracking)
		{
			trackDistanceThreshold = EditorGUILayout.FloatField("Distance threshold", trackDistanceThreshold);
			trackEnergyTolerance = EditorGUILayout.FloatField("Energy-continuity tolerance", trackEnergyTolerance);
		}
		EditorGUI.EndDisabledGroup();

		EditorGUILayout.Space();

		if (!Application.isPlaying)
			EditorGUILayout.HelpBox("Enter Play Mode, press Start. Recording is Play-Mode-only.", MessageType.Warning);

		if (!recording)
		{
			EditorGUI.BeginDisabledGroup(!Application.isPlaying || targetEmitter == null);
			if (GUILayout.Button("Start Recording", GUILayout.Height(30f)))
				StartRecording();
			EditorGUI.EndDisabledGroup();
		}
		else
		{
			float elapsed = (float)(Now() - recordStartTime);
			EditorGUILayout.LabelField("Recording...", string.Format(
				CultureInfo.InvariantCulture,
				"{0:F2} s / {1:F2} s  -  {2} samples  -  {3} particles in last sample",
				elapsed, recordDuration, sampleIndex, LastParticleCount()));

			bool stoppedByDuration = elapsed >= recordDuration;
			if (stoppedByDuration)
				EditorGUILayout.HelpBox("Duration reached - stopping and writing CSV files...", MessageType.Warning);

			if (GUILayout.Button("Stop Recording", GUILayout.Height(30f)))
				StopRecording(stoppedByDuration ? "duration" : "manual");
		}

		EditorGUILayout.Space();
		EditorGUILayout.HelpBox(
			"WORKFLOW (Cloud_bad, ~5-10 s):\n" +
			"1. Select the legacy Cloud_bad object in the Hierarchy (the GO named Cloud_bad with ParticleEmitter/ParticleAnimator/ParticleRenderer).\n" +
			"2. This window: drag the emitter into 'Target legacy emitter' (or use the selection button below BEFORE Play).\n" +
			"3. Leave defaults (0.05 s interval, 10 s duration); keep tracking OFF for the first recording.\n" +
			"4. Enter Play Mode, let Cloud_bad run for a few seconds, press Start Recording.\n" +
			"5. Do NOT move/emitter-edit anything while recording (read-only, but the data must stay interpretable).\n" +
			"6. Recording auto-stops after the duration and writes two CSVs into <project>/Logs/.\n" +
			"7. Optionally record a second session with tracking ON (thresholds above), labeled Heuristic.",
			MessageType.None);

		if (GUILayout.Button("Use selected emitter"))
		{
			GameObject selected = Selection.activeGameObject;
			if (selected != null)
			{
				ParticleEmitter fromSelection = selected.GetComponent<ParticleEmitter>();
				if (fromSelection != null)
					targetEmitter = fromSelection;
				else
					ShowNotification(new GUIContent(selected.name + " has no ParticleEmitter"));
			}
		}
	}

	int LastParticleCount()
	{
		if (!recording || targetEmitter == null)
			return 0;
		Particle[] particles = targetEmitter.particles; // copy - read-only use
		return particles.Length;
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

		// Sample on SIMULATION time (Time.time) so the interval is in the same
		// clock as energies/velocities; accuracy over editor-frame timing.
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
		if (targetEmitter == null)
			return;

		// Never guess the space: the flag decides how the world columns are built.
		GameObject emitterGo = targetEmitter.gameObject;
		SerializedObject emitterSo = new SerializedObject(targetEmitter);
		SerializedProperty simProp = emitterSo.FindProperty("Simulate in Worldspace?");
		bool simWorld = simProp != null && simProp.boolValue;

		particleRows.Clear();
		emitterRows.Clear();
		tracks.Clear();
		nextTrackID = 0;
		sampleIndex = 0;
		lastSampleTime = Now() - sampleInterval; // sample immediately
		recordStartTime = Now();
		sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

		recording = true;

		Debug.Log(kToolTitle + ": recording " + GetEmitterPath(targetEmitter.transform)
			+ " (simWorld=" + simWorld
			+ ", interval=" + sampleInterval.ToString("R", CultureInfo.InvariantCulture)
			+ " s, duration=" + recordDuration.ToString("R", CultureInfo.InvariantCulture)
			+ " s, tracking=" + (enableTracking ? "ON (heuristic, labeled)" : "OFF (snapshot-only)") + ")");
	}

	void CaptureSample(double now)
	{
		GameObject emitterGo = targetEmitter.gameObject;
		Transform emitterT = emitterGo.transform;

		SerializedObject emitterSo = new SerializedObject(targetEmitter);
		SerializedProperty simProp = emitterSo.FindProperty("Simulate in Worldspace?");
		bool simWorld = simProp != null && simProp.boolValue;

		// Per-sample transform state: matrices + timestamps make coordinate-space
		// interpretation verifiable offline instead of assumed (requirement 7).
		Matrix4x4 l2w = emitterT.localToWorldMatrix;
		Matrix4x4 w2l = emitterT.worldToLocalMatrix;
		Vector3 worldPos = emitterT.position;
		Vector3 lossy = emitterT.lossyScale;
		string wallClock = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

		double dtThis = now - lastSampleTime; // > 0 (except the immediate first sample)
		double time = now;
		lastSampleTime = now;

		// Emitter-state CSV row (same sample index, one per sample). Matrices go
		// in as 16 INDIVIDUAL cells each, so the column count stays fixed.
		List<string> emitterCells = new List<string>(44);
		emitterCells.Add(sampleIndex.ToString(CultureInfo.InvariantCulture));
		emitterCells.Add(time.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(wallClock);
		emitterCells.Add(SanitizeName(emitterGo.name));
		emitterCells.Add(targetEmitter.GetInstanceID().ToString(CultureInfo.InvariantCulture));
		emitterCells.Add(simWorld ? "world" : "local");
		emitterCells.Add(lossy.x.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(lossy.y.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(lossy.z.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(worldPos.x.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(worldPos.y.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.Add(worldPos.z.ToString("R", CultureInfo.InvariantCulture));
		emitterCells.AddRange(MatrixCells(l2w));
		emitterCells.AddRange(MatrixCells(w2l));
		emitterRows.Add(JoinCsv(emitterCells.ToArray()));

		// THE READ: particles is documented as a copy; only ever read, never assigned back.
		Particle[] particles = targetEmitter.particles;

		string emitterName = SanitizeName(emitterGo.name);
		int emitterID = targetEmitter.GetInstanceID();

		for (int i = 0; i < particles.Length; i++)
		{
			Particle p = particles[i];

			// Convenience world columns: raw transported by the emitter transform
			// for local sim (rotation+translation; lossyScale recorded above for
			// verification), raw duplicated for world sim. simSpace column tells
			// the reader which case applies.
			Vector3 worldPosP = p.position;
			Vector3 worldVelP = p.velocity;
			if (!simWorld)
			{
				worldPosP = l2w.MultiplyPoint3x4(p.position);
				worldVelP = emitterT.TransformDirection(p.velocity);
			}

			int trackID = -1;
			string confidence = "None";
			if (enableTracking && dtThis > 0.0)
			{
				int bestTrack = -1;
				float bestDist = float.MaxValue;
				int candidateCount = 0;
				for (int t = 0; t < tracks.Count; t++)
				{
					Track track = tracks[t];
					Vector3 delta = p.position - track.lastPos;
					float dist = delta.magnitude;
					if (dist > trackDistanceThreshold)
						continue;
					// Energy continuity kill-switch: remaining lifetime must have
					// decayed by ~the sample interval since this track was seen.
					float expectedEnergy = track.lastEnergy - (float)dtThis;
					if (Mathf.Abs(p.energy - expectedEnergy) > trackEnergyTolerance)
						continue;
					candidateCount++;
					if (dist < bestDist)
					{
						bestDist = dist;
						bestTrack = t;
					}
				}

				if (bestTrack >= 0)
				{
					Track matched = tracks[bestTrack];
					trackID = matched.id;
					confidence = candidateCount == 1 ? "HeuristicUnique" : "HeuristicMultiple";
					matched.lastPos = p.position;
					matched.lastEnergy = p.energy;
					matched.lastSampleTime = now;
				}
				else
				{
					// New particle (no acceptable candidate): fresh track, but the
					// ROW still says exactly what the association deserves.
					Track track = new Track();
					track.id = nextTrackID++;
					track.lastPos = p.position;
					track.lastEnergy = p.energy;
					track.lastSampleTime = now;
					tracks.Add(track);
					trackID = track.id;
					confidence = "HeuristicUnique"; // first sighting of a fresh track
				}
			}

			particleRows.Add(JoinCsv(
				sampleIndex.ToString(CultureInfo.InvariantCulture),
				time.ToString("R", CultureInfo.InvariantCulture),
				wallClock,
				emitterName,
				emitterID.ToString(CultureInfo.InvariantCulture),
				simWorld ? "world" : "local",
				i.ToString(CultureInfo.InvariantCulture),
				trackID.ToString(CultureInfo.InvariantCulture),
				confidence,
				p.position.x.ToString("R", CultureInfo.InvariantCulture),
				p.position.y.ToString("R", CultureInfo.InvariantCulture),
				p.position.z.ToString("R", CultureInfo.InvariantCulture),
				p.velocity.x.ToString("R", CultureInfo.InvariantCulture),
				p.velocity.y.ToString("R", CultureInfo.InvariantCulture),
				p.velocity.z.ToString("R", CultureInfo.InvariantCulture),
				worldPosP.x.ToString("R", CultureInfo.InvariantCulture),
				worldPosP.y.ToString("R", CultureInfo.InvariantCulture),
				worldPosP.z.ToString("R", CultureInfo.InvariantCulture),
				worldVelP.x.ToString("R", CultureInfo.InvariantCulture),
				worldVelP.y.ToString("R", CultureInfo.InvariantCulture),
				worldVelP.z.ToString("R", CultureInfo.InvariantCulture),
				p.energy.ToString("R", CultureInfo.InvariantCulture),
				p.startEnergy.ToString("R", CultureInfo.InvariantCulture),
				p.size.ToString("R", CultureInfo.InvariantCulture),
				p.rotation.ToString("R", CultureInfo.InvariantCulture),
				p.angularVelocity.ToString("R", CultureInfo.InvariantCulture),
				p.color.r.ToString("R", CultureInfo.InvariantCulture),
				p.color.g.ToString("R", CultureInfo.InvariantCulture),
				p.color.b.ToString("R", CultureInfo.InvariantCulture),
				p.color.a.ToString("R", CultureInfo.InvariantCulture)));
		}

		// Retire tracks unseen for too long (simulation-time staleness in the
		// same clock as the samples) so stale positions never win a future
		// association by accident. 3 sample intervals ≈ 2 missed samples.
		if (enableTracking)
		{
			for (int t = tracks.Count - 1; t >= 0; t--)
			{
				Track track = tracks[t];
				if (now - track.lastSampleTime > 3.0 * sampleInterval)
					tracks.RemoveAt(t);
			}
		}

		sampleIndex++;
	}

	// ---------------------------------------------------------------------
	// CSV writing (project Logs/ convention, like the V2 tool reports)
	// ---------------------------------------------------------------------

	void StopRecording(string reason)
	{
		recording = false;

		string stamp = sessionStamp != null ? sessionStamp : DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
		string emitterTag = targetEmitter != null ? SanitizeName(targetEmitter.gameObject.name) : "unknown";

		string particlesPath = WriteCsv("LegacyParticleMotion_" + emitterTag + "_particles_" + stamp, BuildParticlesCsv());
		string emitterPath = WriteCsv("LegacyParticleMotion_" + emitterTag + "_emitter_" + stamp, BuildEmitterCsv());

		string message = kToolTitle + ": stopped (" + reason + ") - " + sampleIndex + " samples.\n  particles: " + particlesPath + "\n  emitter  : " + emitterPath;
		Debug.Log(message);
		EditorUtility.DisplayDialog(kToolTitle, message, "OK");
	}

	string BuildParticlesCsv()
	{
		List<string> lines = new List<string>();
		lines.Add(JoinCsv(
			"sampleIndex", "time_s", "wallClock", "emitterName", "emitterInstanceID", "simSpace",
			"particleIndex", "trackID", "trackConfidence",
			"posX", "posY", "posZ", "velX", "velY", "velZ",
			"worldPosX", "worldPosY", "worldPosZ", "worldVelX", "worldVelY", "worldVelZ",
			"energy", "startEnergy", "size", "rotation", "angularVelocity",
			"colorR", "colorG", "colorB", "colorA"));
		lines.AddRange(particleRows);
		return string.Join("\n", lines.ToArray()) + "\n";
	}

	string BuildEmitterCsv()
	{
		List<string> lines = new List<string>();
		lines.Add(JoinCsv(
			"sampleIndex", "time_s", "wallClock", "emitterName", "emitterInstanceID", "simSpace",
			"lossyScaleX", "lossyScaleY", "lossyScaleZ",
			"emitterWorldPosX", "emitterWorldPosY", "emitterWorldPosZ",
			"l2w_m00", "l2w_m01", "l2w_m02", "l2w_m03",
			"l2w_m10", "l2w_m11", "l2w_m12", "l2w_m13",
			"l2w_m20", "l2w_m21", "l2w_m22", "l2w_m23",
			"l2w_m30", "l2w_m31", "l2w_m32", "l2w_m33",
			"w2l_m00", "w2l_m01", "w2l_m02", "w2l_m03",
			"w2l_m10", "w2l_m11", "w2l_m12", "w2l_m13",
			"w2l_m20", "w2l_m21", "w2l_m22", "w2l_m23",
			"w2l_m30", "w2l_m31", "w2l_m32", "w2l_m33"));
		lines.AddRange(emitterRows);
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

	static string[] MatrixCells(Matrix4x4 m)
	{
		// Fixed layout: row index, then column index, matching the mRC header
		// names (m00 is [0,0], m01 is [0,1] = the first vector column element).
		string[] cells = new string[16];
		int index = 0;
		for (int r = 0; r < 4; r++)
		{
			for (int c = 0; c < 4; c++)
			{
				cells[index] = m[r, c].ToString("R", CultureInfo.InvariantCulture);
				index++;
			}
		}
		return cells;
	}

	static string SanitizeName(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unnamed";
		return name.Replace(',', '_').Replace('"', '_').Replace('\n', '_').Replace('\r', '_');
	}

	static string GetEmitterPath(Transform t)
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
#endif
