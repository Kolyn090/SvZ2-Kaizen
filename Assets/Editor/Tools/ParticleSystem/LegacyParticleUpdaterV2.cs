/*
 * Legacy Particle Updater V2
 *
 * High-fidelity, non-destructive conversion of Unity legacy particle systems
 * (EllipsoidParticleEmitter / ParticleAnimator / ParticleRenderer) into a
 * modern ParticleSystem, written for Unity 2017.4.40f1 as preparation for a
 * 2021.3 migration.
*
 * Derived from the v1.5 community tool "Legacy_Particle_System_Updater.cs"
 * after a code review. Confirmed V1 bugs fixed here:
 *
 *   - Damping curve was stripped to 2 keys (linear) and gated on
 *     `damping < 1.0f`, silently dropping damping==1 systems entirely.
 *   - SizeGrow curve was stripped to 2 keys (linear) for all models.
 *   - `angularVelocity` (the constant component) was never read; only
 *     `rndAngularVelocity` was converted, so systems with rnd==0 lost all spin.
 *   - `tangentVelocity` was never converted at all (totally lost).
 *   - One-shot burst: `burst.cycleCount = 1` was immediately overwritten with
 *     `int.MaxValue`, turning one-shot systems into repeating bursts.
 *   - Existing ParticleSystems on the target GameObject were silently
 *     overwritten on re-runs (no idempotency, no overwrite guard).
 *   - "Upgrade Everything" NRE'd when a legacy emitter lived on a prefab child
 *     (GetComponent on the prefab root returned null mid-batch).
 *
 * Safety model (do not break):
 *
 *   - Original legacy components are NEVER modified, disabled or deleted.
 *   - Each conversion adds ONE new child GameObject named "[PSV2] <legacyName>"
 *     to the legacy GameObject. Deleting that child fully reverts the change
 *     (see "Revert Selected Conversions").
 *   - Existing ParticleSystems are never overwritten. Conversion is refused
 *     if the legacy GameObject already has a ParticleSystem, if an "[PSV2]"
 *     child already exists, or if a conversion record already exists that is
 *     not in status Reverted.
 *   - Conversions are only ever run explicitly from the menus below. There is
 *     no batch "upgrade everything" entry point on purpose.
 *   - Every conversion writes an auditable record asset, including every
 *     legacy serialized field, what was mapped exactly, what was approximated,
 *     and what was unsupported.
 *   - Scene edits are left UNSAVED after conversion for review. Undo support
 *     is registered for every created object.
 *
 * Known approximate / unverified mappings (see class-level notes below and
 * the kDampingModel / kSizeGrowModel / kTangentModel switches):
 *
 *   - tangentVelocity: legacy applies a spawn-time velocity bias tangential to
 *     the ellipsoid surface, computed per particle from its spawn position.
 *     Shuriken (2017.4) has no tangential emission mode. V2 adds a per-axis
 *     +/- bias as VelocityOverLifetime. Trajectory differs; verify in engine.
 *   - velocity magnitude frame (Stage 3 attempt, DISPROVEN in-engine): the
 *     world-absolute reading (authored magnitudes descaled by 1/lossyScale for
 *     local-sim VOLT) was A/B tested and rejected - the converted Cloud_bad
 *     spread dramatically FARTHER than the legacy original, which falsifies the
 *     world frame. Reverted to authored-as-emitter-local magnitudes (H1).
 *     The remaining compactness is being investigated in the tangent-velocity
 *     STRUCTURE (spawn-point-coupled tangential field); magnitude compensation
 *     factors remain forbidden.
 *   - damping: DOCUMENTED semantics (4.6 manual class-ParticleAnimator, mirrored in
 *     the 2017.1 ScriptReference): "A value of 1 means no damping, the particles
 *     will not slow down or accelerate. A value of 0 means particles will stop
 *     immediately. A value of 2 means particles will double their speed every
 *     second." => v(t) = v0 * damping^t (per-second power law).
 *     REVISION NOTE: an earlier V2 build used v(t) = v0 * exp(-damping*t) after
 *     misreading the legacy frame-scaled integration; that model was wrong and
 *     suppressed all motion in the damping=1 systems shipped in this project.
 *     damping=1 is now mapped to NO velocity curve at all (exact identity).
 *   - sizeGrow: legacy growth semantics are not conclusively documented in the
 *     V1 source: per-second compound ("size *= (1+sizeGrow)^t") or additive-linear
 *     ("size += sizeGrow*t"). The V1
 *     v1.2 changelog asserted compound growth; that claim is not verified
 *     here. Both models implemented behind kSizeGrowModel; VERIFY IN ENGINE.
 *   - rotation sign conventions and rnd damping spread (0..rnd vs +/- rnd)
 *     follow the V1 interpretation but are not verified against legacy source.
 *   - rotation axes: legacy LOCAL/WORLD ROTATION AXES orbit particle POSITIONS
 *     around the emitter center (2017 manual: "with the Transform's position as
 *     the center"; "whirlpool-like swirly motions"). Shuriken 2017.4 has no
 *     orbital velocity module (those VOLT parameters only ship in 2019.1+), so
 *     nothing in this tool's module conversion can encode them. V2 optionally
 *     attaches a runtime LegacyParticleOrbit component to the [PSV2] holder via
 *     'Attach Orbit Script (selection)' (disabled by default; OFF until the user
 *     enables it in the Inspector; speed unit undocumented -> calibrated via the
 *     exposed multiplier, no hardcoded factors). Verify in engine.
 *   - optional EXPERIMENTAL velocity multiplier ('Attach Velocity Multiplier
 *     (selection)'): adds a LegacyParticleVelocityMultiplier component to the
 *     [PSV2] holder, defaulting to 1.0 (unchanged baseline). It scales the
 *     module's Velocity over Lifetime from a captured baseline in the
 *     Inspector (no recompile/reconvert; recomputed from the baseline so
 *     repeated edits never accumulate, and 1.0 restores exact conversion
 *     values). Authorized user-driven calibration experiment for Cloud_bad
 *     compactness - audited as experimental, NOT a legacy conversion mapping,
 *     and never a substitute for structural tangent-velocity work.
 *   - optional Stage-4 tangent emitter experiment ('Attach Tangent Emitter
 *     (selection)'): adds a LegacyParticleTangentEmitter component to the
 *     [PSV2] holder WITHOUT changing behavior (enableTangentEmission=false).
 *     When switched on it takes over emission with per-particle tangent
 *     velocity computed from the spawn position (two candidate models:
 *     CrossProduct n x t / ProjectedField t - n(n.t), Inspector-switchable),
 *     disables the VOLT tangent envelope to prevent double-apply, and restores
 *     the captured module state when switched off. Attach restricted to local
 *     sim / continuous / damping==1 systems. Both models are readings of
 *     undocumented legacy behavior - an experiment, not a mapping.
 *   - optional Stage-6 velocity rotation experiment ('Attach Velocity Rotation
 *     (selection)'): adds a LegacyParticleVelocityRotation component to the
 *     [PSV2] holder WITHOUT changing behavior (enableVelocityRotation=false).
 *     Stage 5 MEASURED the legacy Cloud_bad velocity mechanics as the cross-
 *     product rotation dv/dt = localRotationAxis x velocity (unnormalized axis,
 *     rate <= |a| = 0.3464 rad/s for (0.2,0.2,0.2); speed exactly conserved;
 *     per-step fit error 1.35e-4 vs 0.033 for the reversed sign). Switched on,
 *     it rotates every particle's carried velocity by
 *     Quaternion.AngleAxis(|a|*dt, ahat) per frame in LOCAL sim space while the
 *     VOLT module is DISABLED (capture/restore) so the write is not double-
 *     applied by the module's own per-frame velocity; fresh spawns (identified
 *     by Particle.randomSeed) receive one uniform sample of the captured VOLT
 *     constant-family baselines, so the initial-velocity DISTRIBUTION is
 *     unchanged (requirement: no new spawn-velocity model in this stage).
 *     Attach restricted to local sim / continuous / damping==1 / VOLT-enabled
 *     constant-family systems; warns against running it simultaneously with
 *     the Stage-2 orbit or Stage-4 tangent emitter (one knob at a time).
 *     NOT validated in-engine yet - A/B against the legacy original first.
 *   - Color animation: legacy 5-key RGBA32 gradient serialized as packed ints;
 *     reading via SerializedProperty.colorValue is assumed correct for 2017.4
 *     but should be verified visually once per palette family.
 */

#pragma warning disable 618

#if UNITY_2018_3_OR_NEWER
#error "LegacyParticleUpdaterV2 targets Unity 2017.4 only: legacy particle components were removed in later versions. Convert all scenes in 2017.4 first."
#else

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LegacyParticleUpdaterV2
{
	const string kMenuRoot = "Tools/Particle System/";
	const string kMenuConversion = kMenuRoot + "Conversion V2/";
	const string kMenuExperiments = kMenuRoot + "Experiments/";
	const string kPrefix = "[PSV2]";
	const string kToolTitle = "Legacy Particle Updater V2";

	/// <summary>Number of keys sampled for every generated AnimationCurve. Deliberately NOT stripped down to 2 (V1 bug).</summary>
	const int kCurveKeys = 9;

	// ----------------------------------------------------------------------
	// UNCERTAIN MAPPING SWITCHES
	// These pick between candidate formulas that a code review cannot settle.
	// They exist so validation runs can compare models without code changes.
	// Do not trust a chosen model until a visual A/B test has confirmed it.
	// ----------------------------------------------------------------------
	/// <summary>
	/// 1 = DOCUMENTED legacy power law v(t) = v0 * damping^t per second
	///     (4.6 manual: damping 1 = no damping, 2 = speed doubles every second,
	///     0 = stop immediately). damping == 1 maps to untouched constant VOLT.
	///     Exact when the emitter has a single lifetime; otherwise the curve uses
	///     the average lifetime and the per-particle variance is recorded as an
	///     approximation. (The older exp(-damping*t) model was removed as wrong.)
	/// 2 = skip damping entirely and log an unsupported note (safest fallback).
	/// </summary>
	const int kDampingModel = 1;

	/// <summary>
	/// 1 = compound growth size(t) = size0 * (1 + sizeGrow)^(t) (V1 v1.2 claim).
	/// 2 = additive growth size(t) = size0 + sizeGrow * t.
	/// </summary>
	const int kSizeGrowModel = 1;

	/// <summary>
	/// 1 = tangentVelocity approximated as +/- per-axis VelocityOverLifetime.
	/// 2 = skip tangentVelocity entirely and log an unsupported note.
	/// </summary>
	const int kTangentModel = 1;

	// ---------------------------------------------------------------------
	// Menus
	// ---------------------------------------------------------------------

	[MenuItem(kMenuConversion + "Scan (open scenes + prefabs)", false, 200)]
	static void Scan()
	{
		List<string> lines = new List<string>();
		lines.Add("LEGACY PARTICLE V2 SCAN");
		lines.Add("Unity: " + Application.unityVersion);
		lines.Add("Date : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
		lines.Add("Note : covers open scenes and prefab assets. Closed scenes are NOT scanned.");
		lines.Add("");

		int openSceneCount = 0;
		int prefabCount = 0;
		int blockedCount = 0;

		// Open scenes (includes inactive GameObjects via GetRootGameObjects + GetComponentsInChildren(true))
		for (int i = 0; i < EditorSceneManager.sceneCount; i++)
		{
			UnityEngine.SceneManagement.Scene scene = EditorSceneManager.GetSceneAt(i);
			string scenePath = scene.IsValid() && !string.IsNullOrEmpty(scene.path) ? scene.path : "<untitled scene>";
			GameObject[] roots = scene.GetRootGameObjects();

			for (int r = 0; r < roots.Length; r++)
			{
				ParticleEmitter[] emitters = roots[r].GetComponentsInChildren<ParticleEmitter>(true);
				for (int e = 0; e < emitters.Length; e++)
				{
					openSceneCount++;
					lines.Add("OPENSCENE | " + scenePath + " | " + Describe(emitters[e]));
					blockedCount += CountBlocked(emitters[e]);
				}
			}
		}

		// Prefab assets (loaded read-only; nothing is modified)
		string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
		for (int i = 0; i < prefabGuids.Length; i++)
		{
			string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
			GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab == null)
				continue;

			ParticleEmitter[] emitters = prefab.GetComponentsInChildren<ParticleEmitter>(true);
			for (int e = 0; e < emitters.Length; e++)
			{
				prefabCount++;
				blockedCount += CountBlocked(emitters[e]);
				lines.Add("PREFAB    | " + path + " | " + Describe(emitters[e]));
			}
		}

		lines.Add("");
		lines.Add("SUMMARY");
		lines.Add("Open-scene legacy emitters : " + openSceneCount);
		lines.Add("Prefab legacy emitters    : " + prefabCount);
		lines.Add("Blocked by guards         : " + blockedCount);

		lines.Add("");
		lines.Add("RUNTIME CODE REFERENCES (Assets/Scripts only)");
		lines.AddRange(ScanRuntimeReferences());

		string reportPath = WriteLog("LegacyParticlesV2_Scan", lines);

		Debug.Log(kToolTitle + ": scan complete (" + openSceneCount + " open-scene, " + prefabCount +
			" prefab emitters, " + blockedCount + " blocked). Report: " + reportPath);

		EditorUtility.DisplayDialog(kToolTitle,
			"Scan complete.\n\nOpen-scene emitters: " + openSceneCount +
			"\nPrefab emitters: " + prefabCount +
			"\nBlocked by guards: " + blockedCount +
			"\n\nReport written to:\n" + reportPath,
			"OK");
	}

	[MenuItem(kMenuConversion + "Convert Selected (scene objects only)", false, 210)]
	static void ConvertSelected()
	{
		GameObject[] selected = Selection.gameObjects;

		List<ParticleEmitter> targets = new List<ParticleEmitter>();
		List<GameObject> skippedAssetObjects = new List<GameObject>();

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				skippedAssetObjects.Add(go); // prefab assets are not supported by convert v2.0; see note below
				continue;
			}

			ParticleEmitter[] emitters = go.GetComponentsInChildren<ParticleEmitter>(true); // includes inactive children
			for (int e = 0; e < emitters.Length; e++)
				if (!targets.Contains(emitters[e]))
					targets.Add(emitters[e]);
		}

		if (targets.Count == 0 && skippedAssetObjects.Count == 0)
		{
			EditorUtility.DisplayDialog(kToolTitle, "No legacy particle emitters found in the selection.\nSelect scene GameObjects (incl. inactive children) that contain legacy emitters.", "OK");
			return;
		}

		StringBuilder confirm = new StringBuilder();
		confirm.AppendLine("Convert " + targets.Count + " legacy emitter(s) to ParticleSystem children?");
		confirm.AppendLine();
		for (int i = 0; i < targets.Count && i < 20; i++)
			confirm.AppendLine("  " + GetTransformPath(targets[i].transform));
		if (targets.Count > 20)
			confirm.AppendLine("  ... and " + (targets.Count - 20) + " more");
		confirm.AppendLine();
		confirm.AppendLine("Legacy components are preserved. One new [PSV2] child GameObject is added per emitter.");
		confirm.AppendLine("Scene changes are left UNSAVED for review.");
		if (skippedAssetObjects.Count > 0)
			confirm.AppendLine("\nNOTE: " + skippedAssetObjects.Count + " selected asset(s) (prefabs) are skipped; convert them from inside a scene instead.");

		if (!EditorUtility.DisplayDialog(kToolTitle, confirm.ToString(), "Convert", "Cancel"))
			return;

		int created = 0;
		int skipped = 0;
		int failed = 0;
		List<string> log = new List<string>();

		for (int i = 0; i < targets.Count; i++)
		{
			float progress = targets.Count == 1 ? 0.5f : (float)i / targets.Count;
			if (EditorUtility.DisplayCancelableProgressBar(kToolTitle,
				"Converting (" + (i + 1) + "/" + targets.Count + "): " + targets[i].name, progress))
			{
				log.Add("CANCELLED by user at " + GetTransformPath(targets[i].transform));
				break;
			}

			try
			{
				string message;
				if (ConvertOne(targets[i], out message))
				{
					created++;
					log.Add("CONVERTED: " + message);
				}
				else
				{
					skipped++;
					log.Add("SKIPPED  : " + message);
				}
			}
			catch (Exception ex)
			{
				failed++;
				log.Add("FAILED   : " + GetTransformPath(targets[i].transform) + " -> " + ex.GetType().Name + ": " + ex.Message);
				UnityEngine.Debug.LogException(ex);
			}
		}

		EditorUtility.ClearProgressBar();
		AssetDatabase.SaveAssets(); // persists record assets only; scene edits are intentionally NOT saved here

		StringBuilder summary = new StringBuilder();
		summary.AppendLine("Created : " + created);
		summary.AppendLine("Skipped : " + skipped);
		summary.AppendLine("Failed  : " + failed);
		summary.AppendLine();
		summary.Append(string.Join("\n", log.ToArray()));

		StringBuilder console = new StringBuilder();
		console.AppendLine(kToolTitle + " conversion summary");
		console.Append(summary.ToString());
		Debug.Log(console.ToString());

		EditorUtility.DisplayDialog(kToolTitle, summary.ToString(), "OK");
	}

	[MenuItem(kMenuConversion + "Report from records", false, 211)]
	static void Report()
	{
		List<string> lines = new List<string>();
		lines.Add("LEGACY PARTICLE V2 CONVERSION REPORT");
		lines.Add("Unity: " + Application.unityVersion);
		lines.Add("Date : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
		lines.Add("");

		string[] recordGuids = AssetDatabase.FindAssets("t:LegacyParticleConversionRecord");
		int converted = 0;
		int signedOff = 0;

		for (int i = 0; i < recordGuids.Length; i++)
		{
			string path = AssetDatabase.GUIDToAssetPath(recordGuids[i]);
			LegacyParticleConversionRecord rec = AssetDatabase.LoadAssetAtPath<LegacyParticleConversionRecord>(path);
			if (rec == null)
				continue;

			if (rec.status == LegacyParticleConversionStatus.Converted)
				converted++;
			else if (rec.status == LegacyParticleConversionStatus.SignedOff)
				signedOff++;

			lines.Add("RECORD: " + path);
			lines.Add("  status     : " + rec.status);
			lines.Add("  source     : " + rec.sourceAssetPath + " | " + rec.sourceObjectPath);
			lines.Add("  target     : " + rec.targetObjectPath);
			lines.Add("  emitter    : " + rec.legacyEmitterType);
			lines.Add("  created    : " + rec.createdAt + " (Unity " + rec.unityVersion + ")");

			AppendSection(lines, "  EXACT      ", rec.exactMappings);
			AppendSection(lines, "  APPROX     ", rec.approximations);
			AppendSection(lines, "  UNSUPPORTED", rec.unsupported);
			AppendSection(lines, "  WARNING    ", rec.warnings);
			lines.Add("");
		}

		lines.Add("SUMMARY");
		lines.Add("Records: " + recordGuids.Length + "  Converted: " + converted + "  SignedOff: " + signedOff);

		string reportPath = WriteLog("LegacyParticlesV2_Report", lines);

		Debug.Log(kToolTitle + ": report written: " + reportPath);
		EditorUtility.DisplayDialog(kToolTitle,
			"Records: " + recordGuids.Length + "\nConverted: " + converted + "\nSignedOff: " + signedOff +
			"\n\nReport written to:\n" + reportPath, "OK");
	}

	[MenuItem(kMenuConversion + "Revert Selected Conversions", false, 212)]
	static void RevertSelected()
	{
		GameObject[] selected = Selection.gameObjects;
		List<Transform> holders = new List<Transform>();

		for (int i = 0; i < selected.Length; i++)
		{
			if (!selected[i].scene.IsValid())
				continue; // prefab assets: cannot revert safely this way

			Transform parent = selected[i].transform;
			for (int c = 0; c < parent.childCount; c++)
			{
				Transform child = parent.GetChild(c);
				if (child.name.StartsWith(kPrefix, StringComparison.Ordinal))
					holders.Add(child);
			}
		}

		if (holders.Count == 0)
		{
			EditorUtility.DisplayDialog(kToolTitle, "No [PSV2] conversion GameObjects found directly under the selected objects.", "OK");
			return;
		}

		StringBuilder confirm = new StringBuilder();
		confirm.AppendLine("Delete " + holders.Count + " [PSV2] conversion GameObject(s)?");
		confirm.AppendLine();
		for (int i = 0; i < holders.Count; i++)
			confirm.AppendLine("  " + GetTransformPath(holders[i]));
		confirm.AppendLine();
		confirm.AppendLine("Legacy components are never touched by this operation.");
		confirm.AppendLine("Their records will be marked Reverted (not deleted, audit trail kept).");

		if (!EditorUtility.DisplayDialog(kToolTitle, confirm.ToString(), "Revert", "Cancel"))
			return;

		for (int i = 0; i < holders.Count; i++)
		{
			GameObject holder = holders[i].gameObject;
			string recordForHolder = RecordPathForHolder(holder);
			string parentPath = GetTransformPath(holder.transform.parent != null ? holder.transform.parent : holder.transform);

			try
			{
				// Refuse to delete a holder that carries anything besides the conversion (e.g. user work).
				ParticleSystem ps = holder.GetComponent<ParticleSystem>();
				ParticleSystemRenderer psr = holder.GetComponent<ParticleSystemRenderer>();
				// The orbit, the velocity multiplier, the tangent emitter and the
				// velocity rotation ARE part of the V2 optional workflow, so they
				// are allowed here; anything else (user content) still blocks
				// revert. Accepted: the 3 conversion components plus any UNIQUE
				// combination of (at most one of each) orbit / velocity
				// multiplier / tangent emitter / velocity rotation.
				Component[] holderComponents = holder.GetComponents<Component>();
				int extraComponentSupport =
					(holder.GetComponent<LegacyParticleOrbit>() != null ? 1 : 0) +
					(holder.GetComponent<LegacyParticleVelocityMultiplier>() != null ? 1 : 0) +
					(holder.GetComponent<LegacyParticleTangentEmitter>() != null ? 1 : 0) +
					(holder.GetComponent<LegacyParticleVelocityRotation>() != null ? 1 : 0);
				bool onlyConversionComponents =
					ps != null && psr != null && // Transform + ParticleSystem + ParticleSystemRenderer (=3)
					holder.transform.childCount == 0 &&
					holderComponents.Length - 3 == extraComponentSupport;

				if (!onlyConversionComponents)
				{
					Debug.LogWarning(kToolTitle + ": refused to revert " + parentPath + "/" + holder.name +
						" - holder contains components or children beyond ParticleSystem+ParticleSystemRenderer (possible manual work). Inspect and revert manually.");
					continue;
				}

				Undo.DestroyObjectImmediate(holder);

				if (!string.IsNullOrEmpty(recordForHolder))
				{
					LegacyParticleConversionRecord rec =
						AssetDatabase.LoadAssetAtPath<LegacyParticleConversionRecord>(recordForHolder);
					if (rec != null)
					{
						rec.status = LegacyParticleConversionStatus.Reverted;
						EditorUtility.SetDirty(rec);
						AssetDatabase.SaveAssets();
					}
				}

				Debug.Log(kToolTitle + ": reverted " + parentPath);
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogException(ex);
			}
		}

		EditorUtility.DisplayDialog(kToolTitle, "Reverted " + holders.Count + " conversion(s). See console for details.", "OK");
	}

	/// <summary>
	/// Optional Stage-2 feature: attaches LegacyParticleOrbit to [PSV2] holders whose
	/// preserved ParticleAnimator carries nonzero localRotationAxis/worldRotationAxis.
	/// Disabled by default in TWO senses: this menu is never run automatically, and the
	/// attached component ships with enableOrbitalSimulation = false (calibration switch
	/// in the Inspector). Only ever touches [PSV2] holder children; the legacy GameObject
	/// and its components are read but never modified.
	/// </summary>
	[MenuItem(kMenuExperiments + "Attach Orbit Script (selection)", false, 300)]
	static void AttachOrbitScripts()
	{
		GameObject[] selected = Selection.gameObjects;
		List<string> log = new List<string>();
		int attached = 0;
		int skipped = 0;
		int failed = 0;

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				log.Add("SKIPPED  : " + go.name + " (asset object; orbit assists only scene holders)");
				skipped++;
				continue;
			}

			Transform parent = go.transform;
			for (int c = 0; c < parent.childCount; c++)
			{
				Transform child = parent.GetChild(c);
				if (!child.name.StartsWith(kPrefix, StringComparison.Ordinal))
					continue; // only V2-generated holders are ever touched

				float progress = 0.5f;
				if (EditorUtility.DisplayCancelableProgressBar(kToolTitle,
					"Attaching orbit script: " + GetTransformPath(child), progress))
				{
					log.Add("CANCELLED by user at " + GetTransformPath(child));
					break;
				}

				try
				{
					string message;
					if (AttachOrbitToHolder(child, out message))
					{
						attached++;
						log.Add("ATTACHED : " + message);
					}
					else
					{
						skipped++;
						log.Add("SKIPPED  : " + message);
					}
				}
				catch (Exception ex)
				{
					failed++;
					log.Add("FAILED   : " + GetTransformPath(child) + " -> " + ex.GetType().Name + ": " + ex.Message);
					UnityEngine.Debug.LogException(ex);
				}
			}
		}

		EditorUtility.ClearProgressBar();

		string report = "Attached: " + attached + "\nSkipped: " + skipped + "\nFailed: " + failed
			+ "\n\n" + string.Join("\n", log.ToArray());
		ConsoleShouldLog(report);
		EditorUtility.DisplayDialog(kToolTitle, report, "OK");
	}

	/// <summary>
	/// Reads the PRESERVED ParticleAnimator on the legacy parent (read-only) and
	/// configures/attaches LegacyParticleOrbit on the holder. Never called for
	/// non-zero axes; duplicate attachment is refused.
	/// </summary>
	static bool AttachOrbitToHolder(Transform holderT, out string message)
	{
		message = "";
		GameObject holder = holderT.gameObject;

		if (holder.GetComponent<ParticleSystem>() == null)
		{
			message = holder.name + " has no ParticleSystem (not a V2 conversion holder)";
			return false;
		}

		if (holder.GetComponent<LegacyParticleOrbit>() != null)
		{
			message = GetTransformPath(holderT) + " already has a LegacyParticleOrbit component";
			return false;
		}

		Transform legacyT = holderT.parent;
		if (legacyT == null)
		{
			message = GetTransformPath(holderT) + " has no legacy parent transform";
			return false;
		}

		Vector3 localAxis = Vector3.zero;
		Vector3 worldAxis = Vector3.zero;
		ParticleAnimator legacyAnimator = legacyT.GetComponent<ParticleAnimator>(); // preserved component, READ-ONLY use
		if (legacyAnimator != null)
		{
			SerializedObject sao = new SerializedObject(legacyAnimator);
			localAxis = GetVector3(sao, "localRotationAxis");
			worldAxis = GetVector3(sao, "worldRotationAxis");
		}

		if (localAxis.sqrMagnitude <= Mathf.Epsilon && worldAxis.sqrMagnitude <= Mathf.Epsilon)
		{
			message = GetTransformPath(legacyT) + " has no nonzero rotation axes (nothing to orbit)";
			return false;
		}

		LegacyParticleOrbit orbit = Undo.AddComponent<LegacyParticleOrbit>(holder);
		orbit.localRotationAxis = localAxis;
		orbit.worldRotationAxis = worldAxis;
		orbit.orbitalSpeedMultiplier = 1f;   // no compensation factor; calibration happens in the Inspector
		orbit.rotateVelocity = true;          // whirlpool reading; A/B via Inspector
		orbit.enableOrbitalSimulation = false; // OFF by default (Stage-2 gating)

		EditorUtility.SetDirty(orbit);
		EditorUtility.SetDirty(holder);
		EditorSceneManager.MarkSceneDirty(legacyT.gameObject.scene);

		LegacyParticleConversionRecord rec = LoadRecordForHolder(holder);
		if (rec != null)
		{
			rec.approximations.Add("orbital rotation axes local=" + FormatVector(localAxis) + " world=" + FormatVector(worldAxis) + " -> LegacyParticleOrbit component on the [PSV2] holder (positional orbit around the emitter center per the 2017 manual). APPROXIMATION, not exact: speed unit undocumented (calibration via orbitalSpeedMultiplier), velocity-rotation reading toggleable (rotateVelocity), applied post-simulation in LateUpdate. enableOrbitalSimulation starts OFF - enable in the Inspector for the cloud_bad calibration A/B.");
			EditorUtility.SetDirty(rec);
			AssetDatabase.SaveAssets(); // records only; scene edits remain unsaved
		}

		message = GetTransformPath(holderT) + " (orbit attached, OFF; legacy axes local " + FormatVector(localAxis) + ", world " + FormatVector(worldAxis) + ")";
		return true;
	}

	/// <summary>Record lookup by holder (inverse of RecordPathForHolder), null-safe.</summary>
	static LegacyParticleConversionRecord LoadRecordForHolder(GameObject holder)
	{
		string path = RecordPathForHolder(holder);
		if (!string.IsNullOrEmpty(path))
			return AssetDatabase.LoadAssetAtPath<LegacyParticleConversionRecord>(path);
		return null;
	}

	static void ConsoleShouldLog(string text)
	{
		StringBuilder console = new StringBuilder();
		console.AppendLine(kToolTitle + ": orbit script attachment summary");
		console.Append(text);
		UnityEngine.Debug.Log(console.ToString());
	}

	/// <summary>
	/// Experimental calibration workflow (Stage 3b): attaches LegacyParticleVelocityMultiplier
	/// to [PSV2] holders. DEFAULT 1.0 = the conversion baseline UNCHANGED; the multiplier is a
	/// user-driven visual calibration value for the remaining Cloud_bad compactness experiment,
	/// audited in the record as experimental - NEVER as an exact legacy mapping. Runs only on
	/// explicit menu invocation; capture-timestamped baseline; Undo supported; original legacy
	/// components untouched. Revert treats this component as part of the conversion.
	/// </summary>
	[MenuItem(kMenuExperiments + "Attach Velocity Multiplier (selection)", false, 301)]
	static void AttachVelocityMultipliers()
	{
		GameObject[] selected = Selection.gameObjects;
		List<string> log = new List<string>();
		int attached = 0;
		int skipped = 0;
		int failed = 0;

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				log.Add("SKIPPED  : " + go.name + " (asset object; multiplier assists only scene holders)");
				skipped++;
				continue;
			}

			Transform parent = go.transform;
			for (int c = 0; c < parent.childCount; c++)
			{
				Transform child = parent.GetChild(c);
				if (!child.name.StartsWith(kPrefix, StringComparison.Ordinal))
					continue; // only V2-generated holders are ever touched

				try
				{
					string message;
					if (AttachVelocityMultiplierToHolder(child, out message))
					{
						attached++;
						log.Add("ATTACHED : " + message);
					}
					else
					{
						skipped++;
						log.Add("SKIPPED  : " + message);
					}
				}
				catch (Exception ex)
				{
					failed++;
					log.Add("FAILED   : " + GetTransformPath(child) + " -> " + ex.GetType().Name + ": " + ex.Message);
					UnityEngine.Debug.LogException(ex);
				}
			}
		}

		string report = "Attached: " + attached + "\nSkipped: " + skipped + "\nFailed: " + failed
			+ "\n\n" + string.Join("\n", log.ToArray());
		ConsoleShouldLog(report);
		EditorUtility.DisplayDialog(kToolTitle, report, "OK");
	}

	static bool AttachVelocityMultiplierToHolder(Transform holderT, out string message)
	{
		message = "";
		GameObject holder = holderT.gameObject;

		if (holder.GetComponent<ParticleSystem>() == null)
		{
			message = holder.name + " has no ParticleSystem (not a V2 conversion holder)";
			return false;
		}

		if (holder.GetComponent<LegacyParticleVelocityMultiplier>() != null)
		{
			message = GetTransformPath(holderT) + " already has a LegacyParticleVelocityMultiplier component";
			return false;
		}

		LegacyParticleVelocityMultiplier multiplier = Undo.AddComponent<LegacyParticleVelocityMultiplier>(holder);
		// Baseline = the module state right now (normally the fresh conversion output);
		// every later multiplier edit recomputes from THIS state (no accumulation).
		multiplier.CaptureBaseline(holder.GetComponent<ParticleSystem>());
		multiplier.velocityMultiplier = 1f; // default: unchanged behavior until the user scales it

		EditorUtility.SetDirty(multiplier);
		EditorUtility.SetDirty(holder);
		EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene);

		LegacyParticleConversionRecord rec = LoadRecordForHolder(holder);
		if (rec != null)
		{
			rec.approximations.Add("velocity multiplier tool attached (EXPERIMENTAL visual calibration, default 1.0 = unchanged conversion baseline). Baseline Velocity over Lifetime state captured at attach time; every multiplier edit recomputes from that baseline (repeated edits never accumulate, setting 1.0 restores the exact conversion values). Applies only to the VOLT module x/y/z (not emission rate, size, lifetime, shape or the orbit script). NOT part of the exact legacy mapping - chosen values are logged via 'Log Velocity Multiplier (selection)'.");
			EditorUtility.SetDirty(rec);
			EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene); // records only; scene edits remain unsaved
		}

		message = GetTransformPath(holderT) + " (velocity multiplier attached at 1.0; baseline captured)";
		return true;
	}

	/// <summary>
	/// Documents the user's CHOSEN multiplier in the emitter's conversion record as an
	/// experimental calibration value (requirement 11). Read-only on the ParticleSystem;
	/// appends one audit line per holder. Works when selecting the [PSV2] holder itself
	/// or its legacy parent, mirroring the other menus' selection rules.
	/// </summary>
	[MenuItem(kMenuExperiments + "Log Velocity Multiplier (selection)", false, 302)]
	static void LogVelocityMultipliers()
	{
		GameObject[] selected = Selection.gameObjects;
		List<string> log = new List<string>();
		int logged = 0;
		int skipped = 0;

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				log.Add("SKIPPED  : " + go.name + " (asset object)");
				skipped++;
				continue;
			}

			// The multiplier may live on the selected object itself (holder selected
			// directly) or on any direct [PSV2] child (legacy parent selected).
			for (int c = -1; c < go.transform.childCount; c++)
			{
				Transform candidate = (c < 0) ? go.transform : go.transform.GetChild(c);
				if (candidate.name.StartsWith(kPrefix, StringComparison.Ordinal) == false && c >= 0)
					continue;

				LegacyParticleVelocityMultiplier multiplier = candidate.GetComponent<LegacyParticleVelocityMultiplier>();
				if (multiplier == null)
				{
					if (c < 0)
						log.Add("SKIPPED  : " + GetTransformPath(candidate) + " has no velocity multiplier component");
					continue;
				}

				LegacyParticleConversionRecord rec = LoadRecordForHolder(candidate.gameObject);
				if (rec == null)
				{
					log.Add("SKIPPED  : " + GetTransformPath(candidate) + " has no conversion record");
					continue;
				}

				rec.approximations.Add("velocity multiplier logged: " + multiplier.velocityMultiplier.ToString("0.######") + " (EXPERIMENTAL visual calibration value set by the user at " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC; applies to the emitter's converted Velocity over Lifetime only).");
				EditorUtility.SetDirty(rec);
				EditorSceneManager.MarkSceneDirty(candidate.gameObject.scene); // record asset only; scene stays as-is
				logged++;
				log.Add("LOGGED   : " + GetTransformPath(candidate) + " -> " + multiplier.velocityMultiplier.ToString("0.######"));
			}
		}

		string report = "Logged: " + logged + "\nSkipped: "
			+ skipped + "\n\n" + string.Join("\n", log.ToArray());
		ConsoleShouldLog(report);
		EditorUtility.DisplayDialog(kToolTitle, report, "OK");
	}

	/// <summary>
	/// Stage-4 experiment: attaches LegacyParticleTangentEmitter to [PSV2] holders
	/// whose preserved legacy emitter carries nonzero tangentVelocity. Deliberately
	/// RESTRICTED to systems where the experiment is well-defined (see header of
	/// the component): Local simulation space, NOT one-shot, damping == 1 (with
	/// damping != 1 the tangent is baked into damping curves and cannot be
	/// rewritten without touching the validated Stage-1 model). The component
	/// starts with enableTangentEmission = false: attaching never changes behavior
	/// until it is switched on in the Inspector. Read-only on the legacy
	/// components; Undo supported; duplicate attachment refused.
	/// </summary>
	[MenuItem(kMenuExperiments + "Attach Tangent Emitter (selection)", false, 303)]
	static void AttachTangentEmitters()
	{
		GameObject[] selected = Selection.gameObjects;
		List<string> log = new List<string>();
		int attached = 0;
		int skipped = 0;
		int failed = 0;

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				log.Add("SKIPPED  : " + go.name + " (asset object; experiment assists only scene holders)");
				skipped++;
				continue;
			}

			Transform parent = go.transform;
			for (int c = 0; c < parent.childCount; c++)
			{
				Transform child = parent.GetChild(c);
				if (!child.name.StartsWith(kPrefix, StringComparison.Ordinal))
					continue; // only V2-generated holders are ever touched

				try
				{
					string message;
					if (AttachTangentEmitterToHolder(child, out message))
					{
						attached++;
						log.Add("ATTACHED : " + message);
					}
					else
					{
						skipped++;
						log.Add("SKIPPED  : " + message);
					}
				}
				catch (Exception ex)
				{
					failed++;
					log.Add("FAILED   : " + GetTransformPath(child) + " -> " + ex.GetType().Name + ": " + ex.Message);
					UnityEngine.Debug.LogException(ex);
				}
			}
		}

		string report = "Attached: " + attached + "\nSkipped: " + skipped + "\nFailed: " + failed
			+ "\n\n" + string.Join("\n", log.ToArray());
		ConsoleShouldLog(report);
		EditorUtility.DisplayDialog(kToolTitle, report, "OK");
	}

	static bool AttachTangentEmitterToHolder(Transform holderT, out string message)
	{
		message = "";
		GameObject holder = holderT.gameObject;

		ParticleSystem holderPs = holder.GetComponent<ParticleSystem>();
		if (holderPs == null)
		{
			message = holder.name + " has no ParticleSystem (not a V2 conversion holder)";
			return false;
		}

		if (holder.GetComponent<LegacyParticleTangentEmitter>() != null)
		{
			message = GetTransformPath(holderT) + " already has a LegacyParticleTangentEmitter component";
			return false;
		}

		Transform legacyT = holderT.parent;
		if (legacyT == null)
		{
			message = GetTransformPath(holderT) + " has no legacy parent transform";
			return false;
		}

		// All precondition data are read from the PRESERVED legacy components (read-only).
		SerializedObject emitterSo = new SerializedObject(legacyT.GetComponent<ParticleEmitter>());
		SerializedObject animatorSo = new SerializedObject(legacyT.GetComponent<ParticleAnimator>());

		bool oneShot = GetBool(emitterSo, "m_OneShot");
		bool simWorld = GetBool(emitterSo, "Simulate in Worldspace?");
		Vector3 tangentVelocity = GetVector3(emitterSo, "tangentVelocity");
		float damping = GetFloat(animatorSo, "damping");

		if (tangentVelocity.sqrMagnitude <= Mathf.Epsilon)
		{
			message = GetTransformPath(legacyT) + " has no tangentVelocity (nothing to reconstruct)";
			return false;
		}
		if (simWorld)
		{
			message = GetTransformPath(legacyT) + " simulates in WORLD space: the experiment covers local-space systems only (world-space tangent frame mapping intentionally out of scope)";
			return false;
		}
		if (oneShot)
		{
			message = GetTransformPath(legacyT) + " is a one-shot (burst) emitter: the experiment is for continuous emitters only";
			return false;
		}
		if (!Mathf.Approximately(damping, 1f))
		{
			message = GetTransformPath(legacyT) + " has damping=" + Format(damping) + " != 1: the tangent contribution is baked into the Stage-1 damping curves (TwoCurves), outside this experiment's scope";
			return false;
		}

		// The converted VOLT axes must be constant-family, or the without-tangent
		// rewrite would run outside the guarded mode set (defensive: the converter
		// writes TwoConstants/Constant axes here given local sim + damping 1).
		ParticleSystem.VelocityOverLifetimeModule boltCheck = holderPs.velocityOverLifetime;
		if (boltCheck.enabled &&
			(boltCheck.x.mode == ParticleSystemCurveMode.Curve ||
			 boltCheck.x.mode == ParticleSystemCurveMode.TwoCurves ||
			 boltCheck.y.mode == ParticleSystemCurveMode.Curve ||
			 boltCheck.y.mode == ParticleSystemCurveMode.TwoCurves ||
			 boltCheck.z.mode == ParticleSystemCurveMode.Curve ||
			 boltCheck.z.mode == ParticleSystemCurveMode.TwoCurves))
		{
			message = GetTransformPath(holderT) + " Velocity over Lifetime contains curve modes (unexpected for this precondition set) - refusing attach";
			return false;
		}

		LegacyParticleTangentEmitter emitter = Undo.AddComponent<LegacyParticleTangentEmitter>(holder);
		emitter.tangentVelocity = tangentVelocity;
		emitter.localVelocity = GetVector3(emitterSo, "localVelocity");
		emitter.worldVelocity = GetVector3(emitterSo, "worldVelocity");
		emitter.rndVelocity = GetVector3(emitterSo, "rndVelocity");
		emitter.ellipsoid = GetVector3(emitterSo, "m_Ellipsoid");
		emitter.minEmitterRange = GetFloat(emitterSo, "m_MinEmitterRange");
		emitter.minEmission = GetFloat(emitterSo, "minEmission");
		emitter.maxEmission = GetFloat(emitterSo, "maxEmission");
		// enableTangentEmission stays false: Stage 1+2 behavior preserved until switched on.

		EditorUtility.SetDirty(emitter);
		EditorUtility.SetDirty(holder);
		EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene);

		LegacyParticleConversionRecord rec = LoadRecordForHolder(holder);
		if (rec != null)
		{
			rec.approximations.Add("tangent emitter experiment attached - OPTIONAL, OFF (enableTangentEmission=false) until enabled in the Inspector. Two candidate tangent models exist in the Inspector (CrossProduct v=n x t / ProjectedField v=t-n(n.t)); both are readings of UNDOCUMENTED legacy behavior, model A/B is the point of the experiment. Velocity magnitude = authored tangentVelocity geometry (no compensation factor), separately scalable via the component's velocityMagnitudeScale. While ON: emission module disabled (script emits via Emit(EmitParams)), VOLT rewritten WITHOUT the tangent envelope (no double-apply), rate re-rolled per SECOND (legacy re-roll granularity approximated). Attach refused for world-space / one-shot / damping!=1 systems. NOT validated against legacy visuals yet.");
			EditorUtility.SetDirty(rec);
			EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene); // records only; scene edits remain unsaved
		}

		message = GetTransformPath(holderT) + " (tangent emitter attached, OFF; tangent " + FormatVector(tangentVelocity) + ", ellipsoid " + FormatVector(emitter.ellipsoid) + ")";
		return true;
	}

	/// <summary>
	/// Optional Stage-6 experiment: attaches LegacyParticleVelocityRotation to
	/// [PSV2] holders whose preserved ParticleAnimator carries a nonzero
	/// localRotationAxis (Stage 5 measured legacy dv/dt = localRotationAxis x
	/// velocity for exactly these systems). Ships with
	/// enableVelocityRotation=false (exact Stage 1+2 behavior until the user
	/// toggles it). Only ever touches [PSV2] holder children; the legacy
	/// GameObject and its components are read but never modified.
	/// </summary>
	[MenuItem(kMenuExperiments + "Attach Velocity Rotation (selection)", false, 304)]
	static void AttachVelocityRotations()
	{
		GameObject[] selected = Selection.gameObjects;
		List<string> log = new List<string>();
		int attached = 0;
		int skipped = 0;
		int failed = 0;

		for (int i = 0; i < selected.Length; i++)
		{
			GameObject go = selected[i];
			if (!go.scene.IsValid())
			{
				log.Add("SKIPPED  : " + go.name + " (asset object; experiment assists only scene holders)");
				skipped++;
				continue;
			}

			Transform parent = go.transform;
			for (int c = 0; c < parent.childCount; c++)
			{
				Transform child = parent.GetChild(c);
				if (!child.name.StartsWith(kPrefix, StringComparison.Ordinal))
					continue; // only V2-generated holders are ever touched

				try
				{
					string message;
					if (AttachVelocityRotationToHolder(child, out message))
					{
						attached++;
						log.Add("ATTACHED : " + message);
					}
					else
					{
						skipped++;
						log.Add("SKIPPED  : " + message);
					}
				}
				catch (Exception ex)
				{
					failed++;
					log.Add("FAILED   : " + GetTransformPath(child) + " -> " + ex.GetType().Name + ": " + ex.Message);
					UnityEngine.Debug.LogException(ex);
				}
			}
		}

		string report = "Attached: " + attached + "\nSkipped: " + skipped + "\nFailed: " + failed
			+ "\n\n" + string.Join("\n", log.ToArray());
		ConsoleShouldLog(report);
		EditorUtility.DisplayDialog(kToolTitle, report, "OK");
	}

	static bool AttachVelocityRotationToHolder(Transform holderT, out string message)
	{
		message = "";
		GameObject holder = holderT.gameObject;

		ParticleSystem holderPs = holder.GetComponent<ParticleSystem>();
		if (holderPs == null)
		{
			message = holder.name + " has no ParticleSystem (not a V2 conversion holder)";
			return false;
		}

		if (holder.GetComponent<LegacyParticleVelocityRotation>() != null)
		{
			message = GetTransformPath(holderT) + " already has a LegacyParticleVelocityRotation component";
			return false;
		}

		Transform legacyT = holderT.parent;
		if (legacyT == null)
		{
			message = GetTransformPath(holderT) + " has no legacy parent transform";
			return false;
		}

		// All precondition data are read from the PRESERVED legacy components (read-only).
		SerializedObject emitterSo = new SerializedObject(legacyT.GetComponent<ParticleEmitter>());
		SerializedObject animatorSo = new SerializedObject(legacyT.GetComponent<ParticleAnimator>());

		bool oneShot = GetBool(emitterSo, "m_OneShot");
		bool simWorld = GetBool(emitterSo, "Simulate in Worldspace?");
		Vector3 localRotationAxis = GetVector3(animatorSo, "localRotationAxis");
		float damping = GetFloat(animatorSo, "damping");

		if (localRotationAxis.sqrMagnitude <= Mathf.Epsilon)
		{
			message = GetTransformPath(legacyT) + " has no localRotationAxis (no rotation law to reproduce)";
			return false;
		}
		if (simWorld)
		{
			message = GetTransformPath(legacyT) + " simulates in WORLD space: the experiment covers local-space systems only";
			return false;
		}
		if (oneShot)
		{
			message = GetTransformPath(legacyT) + " is a one-shot (burst) emitter: the experiment is for continuous emitters only";
			return false;
		}
		if (!Mathf.Approximately(damping, 1f))
		{
			message = GetTransformPath(legacyT) + " has damping=" + Format(damping) + " != 1: the damping is baked into the Stage-1 VOLT curves (TwoCurves), outside the constant-family precondition";
			return false;
		}

		// The VOLT module must exist and be constant-family: while the experiment
		// runs the module is DISABLED and spawn velocities are replicated by
		// sampling the captured baselines; restore writes the exact captured
		// state back. (Defensive: the converter writes TwoConstants/Constant
		// axes here given local sim + damping 1.)
		ParticleSystem.VelocityOverLifetimeModule voltCheck = holderPs.velocityOverLifetime;
		if (!voltCheck.enabled)
		{
			message = GetTransformPath(holderT) + " has no enabled Velocity over Lifetime (no captured spawn-velocity baseline to replicate) - refusing attach";
			return false;
		}
		if (voltCheck.x.mode == ParticleSystemCurveMode.Curve ||
			voltCheck.x.mode == ParticleSystemCurveMode.TwoCurves ||
			voltCheck.y.mode == ParticleSystemCurveMode.Curve ||
			voltCheck.y.mode == ParticleSystemCurveMode.TwoCurves ||
			voltCheck.z.mode == ParticleSystemCurveMode.Curve ||
			voltCheck.z.mode == ParticleSystemCurveMode.TwoCurves)
		{
			message = GetTransformPath(holderT) + " Velocity over Lifetime contains curve modes (unexpected for this precondition set) - refusing attach";
			return false;
		}

		LegacyParticleVelocityRotation rotation = Undo.AddComponent<LegacyParticleVelocityRotation>(holder);
		rotation.localRotationAxis = localRotationAxis;
		// enableVelocityRotation stays false: Stage 1+2 behavior preserved until switched on.

		EditorUtility.SetDirty(rotation);
		EditorUtility.SetDirty(holder);
		EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene);

		LegacyParticleConversionRecord rec = LoadRecordForHolder(holder);
		if (rec != null)
		{
			rec.approximations.Add("velocity rotation experiment attached - OPTIONAL, OFF (enableVelocityRotation=false) until enabled in the Inspector. Reproduces the STAGE 5 MEASURED legacy velocity rotation dv/dt = localRotationAxis x velocity with the UNNORMALIZED axis (Cloud_bad: (0.2,0.2,0.2), rate omega = |a|*sin(angle(v0,ahat)) <= |a| = 0.3464 rad/s = 19.85 deg/s; |v| conserved; per-step fit error 1.35e-4 vs 0.033 for the reversed sign). Implementation approximations: (1) per-frame Quaternion.AngleAxis(|a|*dt, ahat) rotation in LOCAL sim space - exact rotation of the velocity vector, whereas legacy integrates pos += v*dt per step with the same law (measured advvection residual 1.2e-4/step); (2) while ON the VOLT module is DISABLED so the per-frame velocity writes are the only velocity influence (doc investigation: VOLT adds its own per-frame velocity on top of the carried Particle.velocity; composition order undocumented) - engine-side double-apply behavior still to be validated in the in-engine A/B; (3) fresh spawns identified by Particle.randomSeed get ONE uniform sample of the captured VOLT constant-family baselines, so the initial-velocity DISTRIBUTION is unchanged (no new spawn-velocity model this stage, per Stage 5: all candidate spawn-direction laws were rejected; measured fresh structure is 100% outward radial 0.189 +/- 0.043 plus random-direction tangential 0.133 +/- 0.074, |v0| = 0.239 +/- 0.056); (4) fresh spawns are first seen in the LateUpdate after their birth sim step, so movement starts one frame after emission (~|v0|/frameRate local units); (5) particles alive at takeover start rotating mid-flight WITHOUT velocity rewrites; (6) restored behavior is exact for new spawns; particles alive across a restore keep their rotated velocities until they die (VOLT resumes contributing on top when re-enabled); (7) if a Stage-3b velocity multiplier is attached, spawn velocities come from the rotation component's OWN first-capture baseline, so multiplier edits do not affect spawn velocities mid-experiment; (8) do NOT enable simultaneously with the Stage-2 orbit or Stage-4 tangent emitter (runtime warns; one knob at a time). NOT validated in-engine yet.");
			EditorUtility.SetDirty(rec);
			EditorSceneManager.MarkSceneDirty(holderT.gameObject.scene); // records only; scene edits remain unsaved
		}

		message = GetTransformPath(holderT) + " (velocity rotation attached, OFF; localRotationAxis " + FormatVector(localRotationAxis) + ", rate <= " + Format(localRotationAxis.magnitude) + " rad/s)";
		return true;
	}


	// ---------------------------------------------------------------------
	// Core conversion
	// ---------------------------------------------------------------------

	static bool ConvertOne(ParticleEmitter emitter, out string message)
	{
		message = "";
		GameObject legacyGo = emitter.gameObject;
		bool isSceneObject = legacyGo.scene.IsValid();
		string assetPath = isSceneObject ? legacyGo.scene.path : AssetDatabase.GetAssetPath(emitter);

		if (!isSceneObject && !string.IsNullOrEmpty(assetPath))
		{
			message = "prefab asset conversion is not supported by this tool (" + assetPath + "); convert inside a scene instead";
			return false;
		}

		string objectPath = GetTransformPath(legacyGo.transform);
		string recordName = LegacyParticleConversionRecord.BuildRecordName(
			string.IsNullOrEmpty(assetPath) ? "<untitled>" : assetPath, objectPath);
		string recordAssetPath = LegacyParticleConversionRecord.ResolveRecordPath(recordName);

		// ---- Guard 1: never overwrite a ParticleSystem that exists on the legacy GameObject.
		if (legacyGo.GetComponent<ParticleSystem>() != null)
		{
			message = "existing ParticleSystem found on " + objectPath + " - refusing to touch it (existing systems are never overwritten)";

			LegacyParticleConversionRecord blockedRec =
				AssetDatabase.LoadAssetAtPath<LegacyParticleConversionRecord>(recordAssetPath);
			if (blockedRec == null)
			{
				if (!EnsureRecordsFolder())
					return true;
				blockedRec = ScriptableObject.CreateInstance<LegacyParticleConversionRecord>();
				blockedRec.status = LegacyParticleConversionStatus.Blocked;
				blockedRec.sourceAssetPath = string.IsNullOrEmpty(assetPath) ? "<untitled>" : assetPath;
				blockedRec.sourceObjectPath = objectPath;
				blockedRec.legacyEmitterType = emitter.GetType().Name;
				blockedRec.createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
				blockedRec.unityVersion = Application.unityVersion;
				blockedRec.warnings.Add(message);
				AssetDatabase.CreateAsset(blockedRec, recordAssetPath);
			}
			return false;
		}

		// ---- Guard 2: refuse if a conversion holder already exists on this GameObject.
		if (FindHolderChild(legacyGo.transform) != null)
		{
			message = objectPath + " already has a " + kPrefix + " child (use 'Revert Selected Conversions' to redo)";
			return false;
		}

		// ---- Guard 3: refuse if a non-reverted record already exists for this emitter.
		LegacyParticleConversionRecord existingRecord =
			AssetDatabase.LoadAssetAtPath<LegacyParticleConversionRecord>(recordAssetPath);
		if (existingRecord != null && existingRecord.status != LegacyParticleConversionStatus.Reverted)
		{
			message = "conversion record already exists with status " + existingRecord.status + ": " + recordAssetPath;
			return false;
		}

		EditorUtility.DisplayProgressBar(kToolTitle, "Converting: " + objectPath, 0.3f);

		// ---- Create the isolated holder child (identity local transform).
		GameObject holder = new GameObject(kPrefix + " " + legacyGo.name);
		Undo.RegisterCreatedObjectUndo(holder, "Legacy Particle V2 Conversion");
		holder.transform.SetParent(legacyGo.transform, false);
		holder.SetActive(legacyGo.activeSelf); // mirror active state; works for inactive GameObjects too

		ParticleSystem ps = Undo.AddComponent<ParticleSystem>(holder);
		ParticleSystemRenderer psr = holder.GetComponent<ParticleSystemRenderer>();

		LegacyParticleConversionRecord record = ScriptableObject.CreateInstance<LegacyParticleConversionRecord>();
		record.status = LegacyParticleConversionStatus.Converted;
		record.sourceAssetPath = string.IsNullOrEmpty(assetPath) ? "<untitled>" : assetPath;
		record.sourceObjectPath = objectPath;
		record.legacyEmitterType = emitter.GetType().Name;
		record.targetObjectPath = objectPath + "/" + holder.name;
		record.createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
		record.unityVersion = Application.unityVersion;

		record.warnings.Add("Legacy components were preserved (not modified, not disabled, not deleted). Delete the [PSV2] child to revert.");
		record.warnings.Add("Runtime code that searches GetComponentsInChildren<ParticleSystem> under this hierarchy will now also see the converted system.");
		record.warnings.Add("Runtime scripts that disable legacy ParticleRenderer (e.g. Arrow.cs) will NOT hide the converted system until the cutover stage.");

		try
		{
			ConvertEmitter(emitter, holder, ps, psr, record);
		}
		catch (Exception)
		{
			Undo.DestroyObjectImmediate(holder); // leave the scene untouched on failure
			throw;
		}

		record.exactMappings.Add("NOTE: 'exact' classifications are static-analysis only - every mapping still requires visual verification in-engine.");

		if (!EnsureRecordsFolder())
		{
			Undo.DestroyObjectImmediate(holder);
			throw new Exception("could not create " + LegacyParticleConversionRecord.DefaultRecordsFolder);
		}

		AssetDatabase.CreateAsset(record, recordAssetPath);
		EditorUtility.SetDirty(record);
		EditorSceneManager.MarkSceneDirty(legacyGo.scene);

		message = objectPath + " -> " + record.targetObjectPath + (record.approximations.Count > 0
			? "  [APPROXIMATE: " + record.approximations.Count + " note(s)]" : "  [exact]");
		return true;
	}

	/// <summary>Performs the actual field mapping. All legacy data is read via SerializedObject by serialization name.</summary>
	static void ConvertEmitter(ParticleEmitter emitter, GameObject holder, ParticleSystem ps,
		ParticleSystemRenderer psr, LegacyParticleConversionRecord record)
	{
		Transform holderT = holder.transform;
		SerializedObject so = new SerializedObject(emitter);

		bool oneShot = GetBool(so, "m_OneShot");
		bool simWorld = GetBool(so, "Simulate in Worldspace?");
		bool emitActive = GetBool(so, "m_Emit");

		float minSize = GetFloat(so, "minSize");
		float maxSize = GetFloat(so, "maxSize");
		float minEnergy = GetFloat(so, "minEnergy");
		float maxEnergy = GetFloat(so, "maxEnergy");
		float minEmission = GetFloat(so, "minEmission");
		float maxEmission = GetFloat(so, "maxEmission");
		float emitterVelocityScale = GetFloat(so, "emitterVelocityScale");
		Vector3 localVelocity = GetVector3(so, "localVelocity");
		Vector3 worldVelocity = GetVector3(so, "worldVelocity");
		Vector3 rndVelocity = GetVector3(so, "rndVelocity");
		Vector3 tangentVelocity = GetVector3(so, "tangentVelocity");
		float angularVelocity = GetFloat(so, "angularVelocity");        // FIX vs V1: constant component now read
		float rndAngularVelocity = GetFloat(so, "rndAngularVelocity");
		bool rndRotation = GetBool(so, "rndRotation");
		Vector3 ellipsoid = GetVector3(so, "m_Ellipsoid");
		float minEmitterRange = GetFloat(so, "m_MinEmitterRange");

		var main = ps.main;
		var shape = ps.shape;
		var emission = ps.emission;
		var velocityOverLifetime = ps.velocityOverLifetime;
		var rotationOverLifetime = ps.rotationOverLifetime;
		var colorOverLifetime = ps.colorOverLifetime;
		var forceOverLifetime = ps.forceOverLifetime;
		var textureSheetAnimation = ps.textureSheetAnimation;
		var sizeOverLifetime = ps.sizeOverLifetime;
		var inheritVelocity = ps.inheritVelocity;

		// ---------------- Main ----------------
		main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
		main.startSize = TwoConstants(minSize, maxSize);
		main.startLifetime = TwoConstants(minEnergy, maxEnergy);
		main.startRotation = rndRotation
			? TwoConstants(0f, Mathf.Deg2Rad * 360f)
			: new ParticleSystem.MinMaxCurve(0f);
		main.simulationSpace = simWorld ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
		main.scalingMode = ParticleSystemScalingMode.Shape; // legacy ignores transform scale for particle size; this matches it
		main.duration = Mathf.Max(0.05f, maxEnergy);
		main.loop = !oneShot;
		ps.playOnAwake = emitActive;

		int maxParticles = 256 + Mathf.CeilToInt(Mathf.Max(maxEmission, 0f) * (oneShot ? 2f : Mathf.Max(maxEnergy, 1f) + 2f)) * 3;
		main.maxParticles = Mathf.Clamp(maxParticles, 256, 32768);

		record.exactMappings.Add("startSize  <- minSize/maxSize: " + Format(minSize) + ".." + Format(maxSize));
		record.exactMappings.Add("startLifetime <- minEnergy/maxEnergy: " + Format(minEnergy) + ".." + Format(maxEnergy));
		record.exactMappings.Add("startRotation <- rndRotation=" + rndRotation + " (0..360deg random when true)");
		record.exactMappings.Add("simulationSpace <- 'Simulate in Worldspace?'=" + simWorld);
		record.exactMappings.Add("scalingMode=Shape (legacy did not scale particle size with transform scale)");
		record.exactMappings.Add("playOnAwake <- m_Emit=" + emitActive);
		record.warnings.Add("NOTE: legacy runtime toggling of m_Emit after startup has no automatic Shuriken equivalent; verify no script relies on it for " + holder.name);

		// ---------------- Emission ----------------
		if (oneShot)
		{
			main.loop = false;
			emission.rateOverTime = new ParticleSystem.MinMaxCurve(0f);

			ParticleSystem.Burst burst = new ParticleSystem.Burst();
			burst.time = 0f;
			burst.count = TwoConstants(minEmission, maxEmission);
			burst.cycleCount = 1;  // FIX vs V1 (was overwritten with int.MaxValue -> repeated forever)
			burst.repeatInterval = 0f;
			emission.SetBursts(new ParticleSystem.Burst[] { burst });

			record.exactMappings.Add("oneShot -> single Burst, count " + Format(minEmission) + ".." + Format(maxEmission) + ", cycleCount=1");
		}
		else
		{
			emission.rateOverTime = TwoConstants(minEmission, maxEmission);
			record.approximations.Add("emission rate: legacy re-rolls min..max per emission event; Shuriken min/max rate sampling interval differs - verify perceived grain at low rates (" + Format(minEmission) + ".." + Format(maxEmission) + "/s)");
		}

		// ---------------- Inherit velocity ----------------
		// Documented (4.6 manual, Ellipsoid Particle Emitter): emitterVelocityScale
		// "will only apply if Simulate in World Space is enabled", and it inherits a
		// fraction of the emitter's translation AT SPAWN. Local-simulated systems get
		// no emitter-velocity inheritance at all in legacy (V1 mapped it unconditionally).
		if (!Approximately(emitterVelocityScale, 0f))
		{
			if (simWorld)
			{
				// Approximation: Shuriken InheritVelocity applies a CONTINUOUS fraction of
				// the emitter velocity during the whole particle life; legacy applies a
				// SPAWN-TIME translation inheritance. Dynamics differ while the emitter moves.
				inheritVelocity.enabled = true;
				inheritVelocity.curve = new ParticleSystem.MinMaxCurve(emitterVelocityScale);
				record.approximations.Add("emitterVelocityScale=" + Format(emitterVelocityScale) + " -> InheritVelocity curve. Legacy inherits a fraction of the emitter's translation AT SPAWN (worldspace sim only); Shuriken applies a continuous fraction over the particle life - dynamics differ while the emitter moves - VERIFY with a moving emitter.");
			}
			else
			{
				record.unsupported.Add("emitterVelocityScale=" + Format(emitterVelocityScale) + " is documented as inert unless 'Simulate in Worldspace?' is enabled; this system simulates local, so legacy applies no emitter-velocity inheritance - nothing converted (V1 mapped it unconditionally and wrongly).");
			}
		}

		// ---------------- Velocity over lifetime ----------------
		// Legacy sums localVelocity + worldVelocity + rndVelocity at spawn (V1 mapped only ONE of them via if/else-if).
		// V2 sums all contributions and expresses them in the legacy simulation frame.
		bool hasVelocity =
			localVelocity.sqrMagnitude > Mathf.Epsilon ||
			worldVelocity.sqrMagnitude > Mathf.Epsilon ||
			rndVelocity.sqrMagnitude > Mathf.Epsilon ||
			tangentVelocity.sqrMagnitude > Mathf.Epsilon;

		if (hasVelocity)
		{
			velocityOverLifetime.enabled = true;
			velocityOverLifetime.space = simWorld ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;

			// Local-frame center (localVelocity + optionally worldVelocity rotated into emitter space).
			Vector3 centerLocal = localVelocity;
			if (worldVelocity.sqrMagnitude > Mathf.Epsilon)
				centerLocal += holderT.InverseTransformDirection(worldVelocity); // rotation only; direction, no scale

			Vector3 centerWorld = Vector3.zero;
			if (simWorld)
				centerWorld = holderT.TransformDirection(centerLocal);

			// Per-axis random envelope in emitter space (rndVelocity and tangentVelocity are both spawn-time biases).
			Vector3 envelopeLocal = new Vector3(
				Mathf.Abs(tangentVelocity.x) + Mathf.Abs(rndVelocity.x),
				Mathf.Abs(tangentVelocity.y) + Mathf.Abs(rndVelocity.y),
				Mathf.Abs(tangentVelocity.z) + Mathf.Abs(rndVelocity.z));

			Vector3 velMin;
			Vector3 velMax;
			if (simWorld)
				BoundingRangeWorld(holderT, centerWorld, envelopeLocal, out velMin, out velMax);
			else
			{
				velMin = centerLocal - envelopeLocal;
				velMax = centerLocal + envelopeLocal;
			}

			velocityOverLifetime.x = TwoConstants(velMin.x, velMax.x);
			velocityOverLifetime.y = TwoConstants(velMin.y, velMax.y);
			velocityOverLifetime.z = TwoConstants(velMin.z, velMax.z);

			record.exactMappings.Add("velocityOverLifetime space = " + velocityOverLifetime.space);
			record.exactMappings.Add("localVelocity contribution: " + FormatVector(localVelocity));
			record.exactMappings.Add("worldVelocity contribution: " + FormatVector(worldVelocity) + (worldVelocity.sqrMagnitude > Mathf.Epsilon ? " (converted into " + velocityOverLifetime.space + " frame via holder transform)" : ""));
			record.exactMappings.Add("rndVelocity: " + FormatVector(rndVelocity) + " (as +/- envelope)");
			record.approximations.Add("legacy adds the velocity contributions at SPAWN; Shuriken VelocityOverLifetime applies them over the whole particle life. For constant-velocity contributions this phase coincides; combined with the damping curve this should reproduce legacy motion - VERIFY per emitter.");
			if (tangentVelocity.sqrMagnitude > Mathf.Epsilon)
			{
				if (kTangentModel == 1)
				{
					record.approximations.Add("tangentVelocity=" + FormatVector(tangentVelocity) + ": legacy computes a per-particle tangent to the ellipsoid surface at the spawn point (a swirl). Shuriken 2017.4 has no tangential mode; V2 uses +/- per-axis VelocityOverLifetime envelope. Trajectory distribution differs - VERIFY (worst on 'Cloud'/'Cloud_bad' systems).");
				}
				else
				{
					// kTangentModel == 2 is enforced by not touching tangent here; signal clearly in the record.
					record.unsupported.Add("tangentVelocity=" + FormatVector(tangentVelocity) + " dropped (kTangentModel=2); enable model 1 for the VOLT approximation.");
				}
			}
			if (holderT.lossyScale != Vector3.one)
				record.warnings.Add("holder local/world scale is not (1,1,1): converted value ranges were computed from world/local directions and should be re-verified after any parent scale change.");
		}

		// ---------------- Rotation over lifetime ----------------
		float degMin = angularVelocity - rndAngularVelocity;
		float degMax = angularVelocity + rndAngularVelocity;
		if (!Approximately(angularVelocity, 0f) || !Approximately(rndAngularVelocity, 0f))
		{
			rotationOverLifetime.enabled = true;
			if (Approximately(degMin, degMax))
				rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(Mathf.Deg2Rad * degMax);
			else
				rotationOverLifetime.z = TwoConstants(Mathf.Deg2Rad * degMin, Mathf.Deg2Rad * degMax);
			record.exactMappings.Add("rotationOverLifetime.z base + rnd: " + Format(angularVelocity) + " +/- " + Format(rndAngularVelocity) + " deg/s (V1 dropped the base entirely)");
			record.approximations.Add("legacy rndAngularVelocity range convention (0..rnd symmetric vs +/- rnd) and sign direction are unverified - confirm spin direction against legacy playback.");
		}

		// ---------------- Shape ----------------
		ParticleAnimator animator = emitter.GetComponent<ParticleAnimator>();

		if (emitter is EllipsoidParticleEmitter)
		{
			float maxAbs = Mathf.Max(Mathf.Abs(ellipsoid.x), Mathf.Max(Mathf.Abs(ellipsoid.y), Mathf.Abs(ellipsoid.z)));
			if (maxAbs <= Mathf.Epsilon)
			{
				shape.enabled = false; // legacy (0,0,0) emits from the transform point
				record.exactMappings.Add("shape disabled (ellipsoid=(0,0,0) legacy point emission)");
			}
			else if (ellipsoid.x < 0f || ellipsoid.y < 0f || ellipsoid.z < 0f)
			{
				// FIX vs V1: V1 mirrored the ellipsoid (negative scale = invalid) and lost shell emission entirely.
				shape.enabled = true;
				shape.shapeType = ParticleSystemShapeType.Sphere;
				shape.radius = maxAbs;
				shape.scale = new Vector3(Mathf.Abs(ellipsoid.x) / maxAbs, Mathf.Abs(ellipsoid.y) / maxAbs, Mathf.Abs(ellipsoid.z) / maxAbs);
				record.approximations.Add("ellipsoid=" + FormatVector(ellipsoid) + ": negative legacy axes mean SHELL emission; Shuriken 2017.4 Sphere has no shell-thickness control, so volume emission is used. Expect density near the shell to differ - VERIFY (worst case: 'GateMagicMain').");
			}
			else
			{
				shape.enabled = true;
				shape.shapeType = ParticleSystemShapeType.Sphere;
				shape.radius = maxAbs;
				shape.scale = new Vector3(ellipsoid.x / maxAbs, ellipsoid.y / maxAbs, ellipsoid.z / maxAbs);
				record.exactMappings.Add("shape -> Sphere radius " + Format(maxAbs) + " scale " + FormatVector(shape.scale));
			}

			if (!Approximately(minEmitterRange, 0f))
				record.unsupported.Add("m_MinEmitterRange=" + Format(minEmitterRange) + " (legacy 'spawn inside inner shell' exclusion) has no Shuriken equivalent");
		}
		else
		{
			shape.enabled = false;
			record.unsupported.Add("emitter type " + emitter.GetType().Name + " is not an EllipsoidParticleEmitter; converted as point emission - review manually");
		}

		// ---------------- ParticleAnimator ----------------
		if (animator == null)
		{
			record.warnings.Add("no ParticleAnimator on the legacy GameObject: legacy defaults (constant white color, no forces) expected - verify");
		}
		else
		{
			SerializedObject sao = new SerializedObject(animator);
			CopyDump(sao, record);

			bool animateColor = GetBool(sao, "Does Animate Color?");
			if (animateColor)
			{
				Color[] keys = new Color[5];
				bool namesOk = true;
				for (int i = 0; i < 5; i++)
				{
					bool fallback = false;
					keys[i] = GetColor(sao, "colorAnimation[" + i + "]", out fallback);
					if (fallback)
						namesOk = false; // keep true if any key read cleanly; flag only when fallback parsing was used
				}

				Gradient gradient = new Gradient();
				gradient.colorKeys = new GradientColorKey[]
				{
					new GradientColorKey(keys[0], 0.00f),
					new GradientColorKey(keys[1], 0.25f),
					new GradientColorKey(keys[2], 0.50f),
					new GradientColorKey(keys[3], 0.75f),
					new GradientColorKey(keys[4], 1.00f)
				};
				gradient.alphaKeys = new GradientAlphaKey[]
				{
					new GradientAlphaKey(keys[0].a, 0.00f),
					new GradientAlphaKey(keys[1].a, 0.25f),
					new GradientAlphaKey(keys[2].a, 0.50f),
					new GradientAlphaKey(keys[3].a, 0.75f),
					new GradientAlphaKey(keys[4].a, 1.00f)
				};

				colorOverLifetime.enabled = true;
				colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
				record.exactMappings.Add("colorAnimation 5 keys -> ColorOverLifetime gradient (keys at 0/25/50/75/100%)");
				if (!namesOk)
					record.warnings.Add("colorAnimation[0..4]: serialized 'rgba' int was not a Color property; parsed via packed int - VERIFY palette colors visually.");
			}

			Vector3 linearForce = GetVector3(sao, "force");
			Vector3 rndForce = GetVector3(sao, "rndForce");
			if (linearForce.sqrMagnitude > Mathf.Epsilon || rndForce.sqrMagnitude > Mathf.Epsilon)
			{
				forceOverLifetime.enabled = true;
				forceOverLifetime.randomized = true;
				forceOverLifetime.space = ParticleSystemSimulationSpace.World; // legacy force is applied in world space (unverified)
				forceOverLifetime.x = TwoConstants(-rndForce.x + linearForce.x, rndForce.x + linearForce.x);
				forceOverLifetime.y = TwoConstants(-rndForce.y + linearForce.y, rndForce.y + linearForce.y);
				forceOverLifetime.z = TwoConstants(-rndForce.z + linearForce.z, rndForce.z + linearForce.z);
				record.approximations.Add("force/rndForce mapped with +/- envelope; legacy application interval/semantics unverified - (0,0,0) in all current project data, low risk.");
			}

			// Fix vs V1: legacy `stopSimulation` has no Shuriken equivalent; only autodestruct maps.
			bool stopSimulation = GetBool(sao, "stopSimulation");
			if (stopSimulation)
				record.unsupported.Add("stopSimulation=" + stopSimulation + " has no Shuriken equivalent");

			bool autodestruct = GetBool(sao, "autodestruct");
			if (autodestruct)
			{
				main.stopAction = ParticleSystemStopAction.Destroy;
				record.exactMappings.Add("autodestruct -> main.stopAction=Destroy");
			}

			float sizeGrow = GetFloat(sao, "sizeGrow");
			if (!Approximately(sizeGrow, 0f))
			{
				sizeOverLifetime.enabled = true;
				sizeOverLifetime.size = BuildSizeGrowCurve(sizeGrow, minSize, maxSize, minEnergy, maxEnergy, record);
			}

			// Damping: applied last so it modulates the assembled velocity contributions, mirroring
			// legacy where damping scaled the total velocity each frame.
			// Documented legacy semantics: damping=1 -> NO damping (velocity constant for the
			// whole particle life); damping=2 -> speed doubles every second; damping=0 -> stop.
			// => per-second power law v(t) = v0 * damping^t.
			float damping = GetFloat(sao, "damping");
			if (!Approximately(damping, 0f))
			{
				if (kDampingModel == 2)
				{
					record.unsupported.Add("damping=" + Format(damping) + " skipped (kDampingModel=2 safety fallback)");
				}
				else if (damping < 0f)
				{
					record.unsupported.Add("damping=" + Format(damping) + " negative has no Shuriken analog (Mathf.Pow would produce NaN); mapped as NO damping - VERIFY legacy playback.");
				}
				else
				{
					float refLifetime = 0.5f * (minEnergy + maxEnergy); // per-particle lifetime varies; shared curve cannot encode that
					velocityOverLifetime.x = DampenAxis(velocityOverLifetime.x, damping, refLifetime);
					velocityOverLifetime.y = DampenAxis(velocityOverLifetime.y, damping, refLifetime);
					velocityOverLifetime.z = DampenAxis(velocityOverLifetime.z, damping, refLifetime);

					if (Approximately(damping, 1f))
					{
						// Documented legacy case: velocity is held constant for the whole life.
						// DampenAxis returned the axis unchanged; if a damping pass previously
						// decayed it, re-converting after 'Revert Selected Conversions' fixes it.
						record.exactMappings.Add("damping=1: documented legacy semantics is NO damping (velocity held constant until the particle dies). VOLT left constant - exact.");
					}
					else if (Approximately(minEnergy, maxEnergy))
					{
						record.exactMappings.Add("damping=" + Format(damping) + " -> per-second power law v(t)=v0*damping^t on the velocity curves; exact because the legacy lifetime is single-valued (T=" + Format(refLifetime) + "s; end-of-life speed factor d^T=" + Format(Mathf.Pow(damping, refLifetime)) + ").");
					}
					else
					{
						record.approximations.Add("damping=" + Format(damping) + " -> per-second power law v(u)=v0*damping^(u*avgT(" + Format(refLifetime) + ")s). Legacy applies damping^t over the particle's OWN lifetime (varies " + Format(minEnergy) + ".." + Format(maxEnergy) + "s); a shared normalized curve cannot encode that per-particle variance: short-lived particles appear to damp too little, long-lived too much. VERIFY drift spread in-engine (A/B).");
					}
				}
			}

			// Legacy spin/orbit axes: Shuriken 2017.4 has no orbital velocity; positions
			// cannot be orbited by modules. Enumeration below logs it; the optional
			// 'Attach Orbit Script (selection)' menu adds the scripted approximation.
			Vector3 localAxis = GetVector3(sao, "localRotationAxis");
			Vector3 worldAxis = GetVector3(sao, "worldRotationAxis");
			if (localAxis.sqrMagnitude > Mathf.Epsilon || worldAxis.sqrMagnitude > Mathf.Epsilon)
			{
				record.unsupported.Add("rotation axes localRotationAxis=" + FormatVector(localAxis) + " worldRotationAxis=" + FormatVector(worldAxis) + ": orbital position rotation is NOT representable by any 2017.4 Shuriken module (orbital velocity ships only in 2019.1+). Nothing was encoded by the module conversion - VERIFY after Stage-1 testing by using 'Attach Orbit Script (selection)' on this holder (speed unit undocumented; calibrate with the Inspector multiplier).");
			}
		}

		// ---------------- Renderer ----------------
		ParticleRenderer legacyRenderer = emitter.GetComponent<ParticleRenderer>();
		if (legacyRenderer != null)
		{
			SerializedObject sro = new SerializedObject(legacyRenderer);
			CopyDump(sro, record);

			switch ((LegacyRenderModeV2)GetInt(sro, "m_StretchParticles"))
			{
				case LegacyRenderModeV2.Stretch2D:
				case LegacyRenderModeV2.Stretch3D:
					psr.renderMode = ParticleSystemRenderMode.Stretch;
					record.approximations.Add("legacy stretch submode (2D vs velocity-based 3D) has no direct Shuriken equivalent; both lengthScale and velocityScale were copied - VERIFY stretched systems.");
					break;
				case LegacyRenderModeV2.SortedBillboard:
					psr.renderMode = ParticleSystemRenderMode.Billboard;
					psr.sortMode = ParticleSystemSortMode.Distance;
					record.exactMappings.Add("SortedBillboard -> Billboard + sortMode=Distance");
					break;
				case LegacyRenderModeV2.BillboardFixedHorizontal:
					psr.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
					record.exactMappings.Add("BillboardFixedHorizontal -> HorizontalBillboard");
					break;
				case LegacyRenderModeV2.BillboardFixedVertical:
					psr.renderMode = ParticleSystemRenderMode.VerticalBillboard;
					record.exactMappings.Add("BillboardFixedVertical -> VerticalBillboard");
					break;
				default:
					psr.renderMode = ParticleSystemRenderMode.Billboard;
					record.exactMappings.Add("renderMode Billboard");
					break;
			}

			psr.cameraVelocityScale = GetFloat(sro, "m_CameraVelocityScale");
			psr.lengthScale = GetFloat(sro, "m_LengthScale");
			psr.velocityScale = GetFloat(sro, "m_VelocityScale");
			psr.maxParticleSize = GetFloat(sro, "m_MaxParticleSize"); // V1 omitted nothing here; copied verbatim, incl. the 200 = no-clamp case
			psr.shadowCastingMode = legacyRenderer.shadowCastingMode;
			psr.receiveShadows = legacyRenderer.receiveShadows;
			psr.sharedMaterial = legacyRenderer.sharedMaterial;
			psr.lightProbeUsage = legacyRenderer.lightProbeUsage;
			psr.reflectionProbeUsage = legacyRenderer.reflectionProbeUsage;
			psr.sortingLayerID = legacyRenderer.sortingLayerID;
			psr.sortingOrder = legacyRenderer.sortingOrder;
			record.exactMappings.Add("renderer copy: material/shadows/probes/sorting/cameraVelocityScale/lengthScale/velocityScale/maxParticleSize");

			Material[] legacyMaterials = legacyRenderer.sharedMaterials;
			if (legacyMaterials != null && legacyMaterials.Length > 1 && legacyMaterials[1] != null)
				record.unsupported.Add("secondary particle material (legacy trail material) not converted - Shuriken trails need a TrailModule");

			int xTile = GetInt(sro, "UV Animation.x Tile");
			int yTile = GetInt(sro, "UV Animation.y Tile");
			float cycles = GetFloat(sro, "UV Animation.cycles");
			if (xTile > 1 || yTile > 1)
			{
				textureSheetAnimation.enabled = true;
				textureSheetAnimation.numTilesX = xTile;
				textureSheetAnimation.numTilesY = yTile;
				textureSheetAnimation.cycleCount = Mathf.Max(1, Mathf.RoundToInt(cycles)); // FIX vs V1: round instead of truncate
				record.approximations.Add("UV Animation " + xTile + "x" + yTile + " cycles=" + cycles + " -> TextureSheetAnimation over lifetime; frame timing nuances (e.g. non-integer cycles) - VERIFY (worst case: 'LightningStorm' 4x4 x3).");
			}
			else
			{
				textureSheetAnimation.enabled = false;
			}
		}
		else
		{
			record.warnings.Add("no ParticleRenderer on the legacy GameObject: no render mode/material was copied - verify visibility manually");
		}
	}

	// ---------------------------------------------------------------------
	// Curve and value helpers
	// ---------------------------------------------------------------------

	static ParticleSystem.MinMaxCurve TwoConstants(float min, float max)
	{
		if (Mathf.Approximately(min, max))
			return new ParticleSystem.MinMaxCurve(min);
		return new ParticleSystem.MinMaxCurve(min, max);
	}

	/// <summary>
	/// Applies documented legacy damping to a VOLT axis via a normalized multiplier curve.
	/// Legacy semantics (2017.4 ScriptReference + 4.6 manual): v(t) = v0 * damping^t where
	/// t is SECONDS ("1 gives no damping; 2 means particles double their speed every second;
	/// 0 means particles stop immediately").
	/// The Shuriken curve is normalized by lifetime u = t/T, so the curve is
	///     v(u) = v0 * damping^(u * T_ref)
	/// which is exact when the legacy emitter had a single lifetime (minEnergy == maxEnergy,
	/// caller records it); otherwise T_ref = average lifetime and the per-particle variance
	/// is a recorded approximation.
	/// damping == 1 -> identity: the documented "no damping" case, axis returned untouched.
	/// Mathf.Pow(0, 0) == 1 at u=0 and 0 for u&gt;0 ("stop immediately").
	/// Deliberately keeps all kCurveKeys keys: V1 stripped curves to 2 keys (fidelity bug).
	/// </summary>
	static ParticleSystem.MinMaxCurve DampenAxis(ParticleSystem.MinMaxCurve axis, float damping, float refLifetime)
	{
		float minVal;
		float maxVal;
		if (!TryGetAxisRange(axis, out minVal, out maxVal))
			return axis; // e.g. curve modes we did not author ourselves: leave untouched and let the caller record note

		if (Mathf.Approximately(minVal, 0f) && Mathf.Approximately(maxVal, 0f))
			return axis;

		if (Mathf.Approximately(damping, 1f))
			return axis; // documented legacy case: no damping at all - keep the constant velocity

		if (damping < 0f)
			return axis; // negative damping is already logged as unsupported by the caller; never emit NaN curves

		float lifetime = Mathf.Max(refLifetime, Mathf.Epsilon);

		AnimationCurve curve = new AnimationCurve();

		if (Mathf.Approximately(minVal, maxVal))
		{
			for (int i = 0; i < kCurveKeys; i++)
			{
				float u = (float)i / (kCurveKeys - 1);
				float v = Mathf.Pow(damping, u * lifetime); // = exp(ln(damping) * u * T)
				curve.AddKey(u, v);
			}
			return new ParticleSystem.MinMaxCurve(maxVal, curve);
		}

		AnimationCurve curveMin = new AnimationCurve();
		AnimationCurve curveMax = new AnimationCurve();

		// Multiplier m with curve(u=0) reproducing the exact axis bounds:
		//   max(0) = m * maxCurve(0) = maxVal; min(0) = m * minCurve(0) = minVal.
		float multiplier = Mathf.Abs(maxVal) > Mathf.Epsilon ? maxVal : minVal;
		float maxScale = Mathf.Abs(multiplier) < Mathf.Epsilon ? 0f : maxVal / multiplier;
		float minScale = Mathf.Abs(multiplier) < Mathf.Epsilon ? 0f : minVal / multiplier;

		for (int i = 0; i < kCurveKeys; i++)
		{
			float u = (float)i / (kCurveKeys - 1);
			float v = Mathf.Pow(damping, u * lifetime);
			curveMax.AddKey(u, maxScale * v);
			curveMin.AddKey(u, minScale * v);
		}

		return new ParticleSystem.MinMaxCurve(multiplier, curveMin, curveMax);
	}

	static bool TryGetAxisRange(ParticleSystem.MinMaxCurve axis, out float min, out float max)
	{
		min = 0f;
		max = 0f;
		if (axis.mode == ParticleSystemCurveMode.Constant)
		{
			min = axis.constant;
			max = axis.constant;
			return true;
		}
		if (axis.mode == ParticleSystemCurveMode.TwoConstants)
		{
			min = axis.constantMin;
			max = axis.constantMax;
			return true;
		}
		return false;
	}

	/// <summary>
	/// SizeGrow curve builder. Two candidate models survive behind kSizeGrowModel:
	/// 1 = compound growth  size(t) = size0 * (1+sizeGrow)^t      (V1 v1.2 claim, unverified)
	/// 2 = additive growth  size(t) = size0 + sizeGrow * t
	/// Both are sampled with kCurveKeys keys and are NOT stripped (V1 bug).
	/// </summary>
	static ParticleSystem.MinMaxCurve BuildSizeGrowCurve(float sizeGrow, float minSize, float maxSize,
		float minTime, float maxTime, LegacyParticleConversionRecord record)
	{
		float minT = Mathf.Max(minTime, 0.01f);
		float maxT = Mathf.Max(maxTime, minT, 0.01f);

		if (kSizeGrowModel == 2)
		{
			// Additive model: size(t) = size0 + sizeGrow * t
			float normalizer = maxSize + sizeGrow * maxT;
			if (Mathf.Abs(normalizer) < Mathf.Epsilon)
			{
				record.warnings.Add("sizeGrow=" + Format(sizeGrow) + " additive normalizer collapsed to 0; clamped to 1e-3 - VERIFY curve manually");
				normalizer = 0.001f;
			}

			AnimationCurve curveMax = new AnimationCurve();
			AnimationCurve curveMin = new AnimationCurve();
			bool minEqualsMaxTime = Mathf.Approximately(minT, maxT);

			for (int i = 0; i < kCurveKeys; i++)
			{
				float u = (float)i / (kCurveKeys - 1);
				curveMax.AddKey(u, (maxSize + sizeGrow * u * maxT) / normalizer);
				if (!minEqualsMaxTime)
					curveMin.AddKey(u, (minSize + sizeGrow * u * minT) / normalizer);
			}

			if (Mathf.Approximately(minSize, maxSize) && minEqualsMaxTime)
			{
				// exact representation when sizes and lifetimes do not vary
				record.exactMappings.Add("sizeGrow=" + Format(sizeGrow) + " -> additive growth, exact for constant size/lifetime [model 2]");
				return new ParticleSystem.MinMaxCurve(normalizer, curveMax);
			}

			record.approximations.Add("sizeGrow=" + Format(sizeGrow) + " additive model with varying start size/lifetime yields bounding curves only [model 2] - VERIFY");
			return new ParticleSystem.MinMaxCurve(normalizer, curveMin, curveMax);
		}

		// Compound model: size(t) = size0 * (1 + sizeGrow)^t
		float baseVal = 1f + sizeGrow;
		if (baseVal < 0.01f)
		{
			record.warnings.Add("sizeGrow=" + Format(sizeGrow) + " -> compound base clamped from " + baseVal + " to 0.01 - VERIFY curve manually");
			baseVal = 0.01f;
		}

		if (Mathf.Approximately(minTime, maxTime))
		{
			float finalSize = Mathf.Pow(baseVal, maxT);
			AnimationCurve curve = new AnimationCurve();
			for (int i = 0; i < kCurveKeys; i++)
			{
				float u = (float)i / (kCurveKeys - 1);
				curve.AddKey(u, Mathf.Pow(baseVal, u * maxT) / finalSize);
			}
			record.exactMappings.Add("sizeGrow=" + Format(sizeGrow) + " -> compound growth, multiplier " + Format(finalSize) + "[model 1] - VERIFY growth shape in-engine");
			return new ParticleSystem.MinMaxCurve(finalSize, curve);
		}

		float finalMax = Mathf.Pow(baseVal, maxT);
		AnimationCurve curveMaxC = new AnimationCurve();
		AnimationCurve curveMinC = new AnimationCurve();
		for (int i = 0; i < kCurveKeys; i++)
		{
			float u = (float)i / (kCurveKeys - 1);
			curveMaxC.AddKey(u, Mathf.Pow(baseVal, u * maxT) / finalMax);
			curveMinC.AddKey(u, Mathf.Pow(baseVal, u * minT) / finalMax);
		}
		record.approximations.Add("sizeGrow=" + Format(sizeGrow) + " compound model with lifetime spread " + Format(minTime) + ".." + Format(maxTime) + " uses bounding min/max curves per V1 approach [model 1] - VERIFY");
		return new ParticleSystem.MinMaxCurve(finalMax, curveMinC, curveMaxC);
	}

	/// <summary>World-frame bounding box of center +/- envelopeLocal rotated by the transform's rotation.</summary>
	static void BoundingRangeWorld(Transform t, Vector3 centerWorld, Vector3 envelopeLocal, out Vector3 min, out Vector3 max)
	{
		Vector3 envelopeWorld = t.TransformDirection(envelopeLocal); // rotation only
		// 8 corners of the local envelope rotated by the emitter rotation (approximation for non-axis-aligned envelopes).
		min = centerWorld;
		max = centerWorld;
		for (int i = 0; i < 8; i++)
		{
			Vector3 sign = new Vector3(
				((i & 1) != 0) ? 1f : -1f,
				((i & 2) != 0) ? 1f : -1f,
				((i & 4) != 0) ? 1f : -1f);
			Vector3 corner = centerWorld + Vector3.Scale(sign, envelopeWorld);
			min = Vector3.Min(min, corner);
			max = Vector3.Max(max, corner);
		}
	}

	// ---------------------------------------------------------------------
	// Serialized field access + dump
	// ---------------------------------------------------------------------

	static float GetFloat(SerializedObject so, string name)
	{
		SerializedProperty p = so.FindProperty(name);
		return p != null && p.propertyType == SerializedPropertyType.Float ? p.floatValue : 0f;
	}

	static bool GetBool(SerializedObject so, string name)
	{
		SerializedProperty p = so.FindProperty(name);
		return p != null && p.propertyType == SerializedPropertyType.Boolean && p.boolValue;
	}

	static int GetInt(SerializedObject so, string name)
	{
		SerializedProperty p = so.FindProperty(name);
		return p != null ? p.intValue : 0;
	}

	static Vector3 GetVector3(SerializedObject so, string name)
	{
		SerializedProperty p = so.FindProperty(name);
		return p != null && p.propertyType == SerializedPropertyType.Vector3 ? p.vector3Value : Vector3.zero;
	}

	/// <summary>
	/// Reads a legacy color key. Legacy Color32 keys serialize as a packed "rgba" integer
	/// (e.g. colorAnimation[0]: rgba 184016093). Try SerializedProperty.colorValue first
	/// (worked in V1); fall back to parsing pack order (A,R,G,B) with a verification warning.
	/// </summary>
	static Color GetColor(SerializedObject so, string name, out bool usedFallback)
	{
		usedFallback = false;
		SerializedProperty p = so.FindProperty(name);
		if (p == null)
			return Color.white;

		if (p.propertyType == SerializedPropertyType.Color)
			return p.colorValue; // V1-compatible path

		// Legacy path: children like "colorAnimation[0].rgba" hold the packed int.
		// Pack order assumed below as (A<<24 | R<<16 | G<<8 | B); VERIFY palette visually
		// and invert the byte order here if the converted gradient looks wrong.
		SerializedProperty child = so.FindProperty(name + ".rgba");
		if (child != null)
		{
			usedFallback = true;
			// intValue sign-extends the stored 32-bit pattern; masking to 8 bits is
			// sign-extension proof, so the byte layout below stays correct even for
			// packed values > Int32.MaxValue (e.g. 4292705390 arrives negative).
			// (SerializedProperty.longValue is avoided for 2017.4 API safety.)
			long rgba = child.intValue;
			byte a = (byte)((rgba >> 24) & 0xFF);
			byte r = (byte)((rgba >> 16) & 0xFF);
			byte g = (byte)((rgba >> 8) & 0xFF);
			byte b = (byte)(rgba & 0xFF);
			return new Color32(r, g, b, a);
		}
		return Color.white;
	}

	/// <summary>Raw dump of every readable serialized property at depth &lt;= 2 for audit.</summary>
	static void CopyDump(SerializedObject so, LegacyParticleConversionRecord record)
	{
		try
		{
			SerializedProperty it = so.GetIterator();
			while (it.Next(true))
			{
				if (it.depth > 2)
					continue;
				if (it.name == "m_Script" || it.name == "m_GameObject")
					continue;

				string value;
				switch (it.propertyType)
				{
					case SerializedPropertyType.Integer:
						// intValue instead of longValue for 2017.4 safety; packed values > Int32.MaxValue display negative (audit-only impact)
						value = it.intValue.ToString();
						break;
					case SerializedPropertyType.Boolean:
						value = it.boolValue ? "true" : "false";
						break;
					case SerializedPropertyType.Float:
						value = it.floatValue.ToString("R");
						break;
					case SerializedPropertyType.Vector3:
						value = FormatVector(it.vector3Value);
						break;
					case SerializedPropertyType.Color:
						{
							Color c = it.colorValue;
							value = "Color(" + c.r.ToString("R") + ", " + c.g.ToString("R") + ", " + c.b.ToString("R") + ", " + c.a.ToString("R") + ")";
						}
						break;
					case SerializedPropertyType.ObjectReference:
						value = it.objectReferenceValue != null ? it.objectReferenceValue.ToString() : "null";
						break;
					case SerializedPropertyType.Generic:
						continue; // struct containers; their leaves appear as separate entries
					default:
						value = "<" + it.propertyType + ">";
						break;
				}

				LegacyParticleFieldEntry entry = new LegacyParticleFieldEntry();
				entry.field = it.propertyPath;
				entry.value = value;
				record.legacyFields.Add(entry);
			}
		}
		catch (Exception ex)
		{
			record.warnings.Add("legacy field dump incomplete: " + ex.Message);
		}
	}

	// ---------------------------------------------------------------------
	// Scan helpers
	// ---------------------------------------------------------------------

	/// <summary>Returns 1 when the emitter is protected from conversion by the overwrite guards, else 0.</summary>
	static int CountBlocked(ParticleEmitter emitter)
	{
		GameObject go = emitter.gameObject;
		if (go.GetComponent<ParticleSystem>() != null)
			return 1;
		if (FindHolderChild(go.transform) != null)
			return 1;
		return 0;
	}

	static string Describe(ParticleEmitter emitter)
	{
		GameObject go = emitter.gameObject;
		StringBuilder sb = new StringBuilder();

		sb.Append("name=").Append(go.name);
		sb.Append("  type=").Append(emitter.GetType().Name);
		sb.Append("  activeSelf=").Append(go.activeSelf);

		try
		{
			SerializedObject so = new SerializedObject(emitter);
			bool oneShot = GetBool(so, "m_OneShot");
			bool simWorld = GetBool(so, "Simulate in Worldspace?");
			Vector3 ellipsoid = GetVector3(so, "m_Ellipsoid");
			ParticleAnimator animator = go.GetComponent<ParticleAnimator>();

			sb.Append("  oneShot=").Append(oneShot);
			sb.Append("  simWorld=").Append(simWorld);
			sb.Append("  size=").Append(Format(GetFloat(so, "minSize"))).Append("..").Append(Format(GetFloat(so, "maxSize")));
			sb.Append("  energy=").Append(Format(GetFloat(so, "minEnergy"))).Append("..").Append(Format(GetFloat(so, "maxEnergy")));
			sb.Append("  emission=").Append(Format(GetFloat(so, "minEmission"))).Append("..").Append(Format(GetFloat(so, "maxEmission")));
			sb.Append("  tangentV=").Append(FormatVector(GetVector3(so, "tangentVelocity")));
			sb.Append("  localV=").Append(FormatVector(GetVector3(so, "localVelocity")));
			sb.Append("  worldV=").Append(FormatVector(GetVector3(so, "worldVelocity")));
			sb.Append("  rndV=").Append(FormatVector(GetVector3(so, "rndVelocity")));
			sb.Append("  emitVScale=").Append(Format(GetFloat(so, "emitterVelocityScale")));
			sb.Append("  ellipsoid=").Append(FormatVector(ellipsoid));
			sb.Append("  angularV=").Append(Format(GetFloat(so, "angularVelocity")));
			sb.Append("  rndAngularV=").Append(Format(GetFloat(so, "rndAngularVelocity")));

			sb.Append("  animator=").Append(animator != null ? "yes" : "NO");
			if (animator != null)
			{
				SerializedObject sao = new SerializedObject(animator);
				sb.Append("  damping=").Append(Format(GetFloat(sao, "damping")));
				sb.Append("  sizeGrow=").Append(Format(GetFloat(sao, "sizeGrow")));
			}

			ParticleRenderer legacyRenderer = go.GetComponent<ParticleRenderer>();
			sb.Append("  renderer=").Append(legacyRenderer != null ? "yes" : "NO");
			if (legacyRenderer != null)
			{
				SerializedObject sro = new SerializedObject(legacyRenderer);
				sb.Append("  stretch=").Append(GetInt(sro, "m_StretchParticles"));
				sb.Append("  uvTiles=").Append(GetInt(sro, "UV Animation.x Tile")).Append("x").Append(GetInt(sro, "UV Animation.y Tile"));
			}

			bool hasPS = go.GetComponent<ParticleSystem>() != null;
			bool hasHolder = FindHolderChild(go.transform) != null;
			sb.Append(hasPS ? "  [HAS ParticleSystem - conversion blocked]" : "");
			sb.Append(hasHolder ? "  [HAS " + kPrefix + " child - already converted]" : "");
		}
		catch (Exception ex)
		{
			sb.Append("  <describe failed: ").Append(ex.Message).Append(">");
		}

		return sb.ToString();
	}

	/// <summary>Scans Assets/Scripts for direct legacy-type usage patterns (compile blockers + cutover checklist).</summary>
	static List<string> ScanRuntimeReferences()
	{
		List<string> result = new List<string>();
		string scriptsDir = Path.GetFullPath("Assets/Scripts");
		if (!Directory.Exists(scriptsDir))
			return result;

		try
		{
			string[] files = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories);
			for (int f = 0; f < files.Length; f++)
			{
				string text;
				try { text = File.ReadAllText(files[f]); }
				catch { continue; }

				bool refsRenderer = text.Contains("ParticleRenderer");
				bool refsEmitter = text.Contains("ParticleEmitter");
				bool refsAnimator = text.Contains("ParticleAnimator");
				if (refsRenderer || refsEmitter || refsAnimator)
				{
					StringBuilder sb = new StringBuilder();
					sb.Append("  ").Append(files[f]);
					sb.Append(" ->");
					if (refsEmitter) sb.Append(" ParticleEmitter");
					if (refsAnimator) sb.Append(" ParticleAnimator");
					if (refsRenderer) sb.Append(" ParticleRenderer");
					result.Add(sb.ToString());
				}
			}
		}
		catch (Exception ex)
		{
			result.Add("  <runtime reference scan failed: " + ex.Message + ">");
		}

		if (result.Count == 0)
			result.Add("  (none found in Assets/Scripts)");

		return result;
	}

	// ---------------------------------------------------------------------
	// Small utilities
	// ---------------------------------------------------------------------

	static bool Approximately(float a, float b)
	{
		return Mathf.Approximately(a, b);
	}

	static Transform FindHolderChild(Transform legacy)
	{
		for (int i = 0; i < legacy.childCount; i++)
		{
			Transform child = legacy.GetChild(i);
			if (child.name.StartsWith(kPrefix, StringComparison.Ordinal))
				return child;
		}
		return null;
	}

	static string GetTransformPath(Transform t)
	{
		StringBuilder sb = new StringBuilder();
		sb.Append(t.name);
		while (t.parent != null)
		{
			t = t.parent;
			sb.Insert(0, t.name + "/");
		}
		return "/" + sb.ToString();
	}

	static bool EnsureRecordsFolder()
	{
		// Path knowledge lives in LegacyParticleConversionRecord (kRecordFolders).
		return LegacyParticleConversionRecord.EnsureDefaultRecordsFolder();
	}

	static string RecordPathForHolder(GameObject holder)
	{
		Transform legacyT = holder.transform.parent;
		if (legacyT == null)
			return "";
		GameObject legacyGo = legacyT.gameObject;
		string assetPath = legacyGo.scene.IsValid() ? legacyGo.scene.path : AssetDatabase.GetAssetPath(legacyGo);
		return LegacyParticleConversionRecord.ResolveRecordPath(LegacyParticleConversionRecord.BuildRecordName(
			string.IsNullOrEmpty(assetPath) ? "<untitled>" : assetPath, GetTransformPath(legacyT)));
	}

	static string Format(float f)
	{
		return f.ToString("R");
	}

	static string FormatVector(Vector3 v)
	{
		return "(" + v.x.ToString("R") + ", " + v.y.ToString("R") + ", " + v.z.ToString("R") + ")";
	}

	static void AppendSection(List<string> lines, string prefix, List<string> items)
	{
		if (items == null || items.Count == 0)
			return;
		for (int i = 0; i < items.Count; i++)
		{
			string text = items[i];
			int nl = text.IndexOf('\n');
			if (nl >= 0)
				text = text.Substring(0, nl).Trim();
			lines.Add(prefix + " " + text);
		}
	}

	static string WriteLog(string baseName, List<string> lines)
	{
		try
		{
			string logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
			if (!Directory.Exists(logsDir))
				Directory.CreateDirectory(logsDir);

			string path = Path.Combine(logsDir, baseName + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".txt");
			File.WriteAllText(path, string.Join("\n", lines.ToArray()) + Environment.NewLine);
			return path;
		}
		catch (Exception ex)
		{
			Debug.LogError(kToolTitle + ": writing report failed: " + ex.Message);
			return "<write failed: " + ex.Message + ">";
		}
	}

	/// <summary>Legacy renderer modes in serialization order (renamed vs V1 to avoid clashes).</summary>
	enum LegacyRenderModeV2
	{
		Billboard = 0,
		Stretch2D = 1,
		SortedBillboard = 2,
		Stretch3D = 3,
		BillboardFixedHorizontal = 4,
		BillboardFixedVertical = 5,
	}
}

#endif

#pragma warning restore 618
