using UnityEngine;

/*
 * LegacyParticleVelocityRotation (runtime component, created by LegacyParticleUpdaterV2)
 *
 * STAGE 6 - optional, reversible experiment: reproduces the velocity rotation that
 * Stage 5 measured in the legacy original (Cloud_bad recording, 1988 rows / 189
 * snapshots). MEASURED LAW (fit to the recording, per-step error 1.35e-4 vs
 * 0.033 for the reversed handedness = 240x separation):
 *
 *     dv/dt = localRotationAxis x velocity        (axis UNNORMALIZED)
 *
 *   For Cloud_bad localRotationAxis = (0.2, 0.2, 0.2) => rotation rate
 *   omega = |a| * sin(angle(v0hat, ahat)) <= |a| = 0.3464 rad/s (19.85 deg/s);
 *   speed is exactly conserved (v . a is invariant; measured drift +3.7e-6/step).
 *   This constant rotation of the velocity direction is what bends the legacy
 *   trajectories (curvature radius ~0.77 local units; full loop period
 *   2pi/|a| ~ 18.1 s > lifetime, so particles draw 60..110 deg arcs).
 *
 * IMPLEMENTATION (requirement 6 - quaternion, minimal speed drift):
 *
 *   Every LateUpdate while the experiment is ON, each particle's velocity is
 *   rotated by
 *
 *       Quaternion.AngleAxis(|a| * Time.deltaTime * Rad2Deg, a.normalized)
 *
 *   applied in emitter-LOCAL space (attach refuses world-space simulation).
 *   Rotating a vector by a quaternion preserves its magnitude to float
 *   precision - strictly better than an Euler v += (a x v) * dt step, which
 *   grows |v|. The measured law itself is Euler-grade integrated by legacy
 *   (pos' = pos + vel*dt, residual 1.2e-4) against an exact-rotation velocity
 *   update, so the quaternion step is the most faithful available integrator.
 *
 * HOW THE VELOCITY WRITE SURVIVES SHURIKEN (requirement 7 + 8 - doc
 * investigated FIRST, per the stage requirements, 2017.4 ScriptReference):
 *
 *   - ParticleSystem.Particle.velocity is persistent per-particle state:
 *     GetParticles/SetParticles read and write the full particle buffer and a
 *     written velocity carries into the next simulation step (which advances
 *     pos += velocity * dt).
 *   - The Velocity over Lifetime module ADDS its own velocity per frame on top
 *     of the particle's carried velocity (module docs list x/y/z/enabled/space/
 *     speedModifier but do NOT document the composition order with the carried
 *     velocity). Leaving VOLT enabled would double-apply motion.
 *     ENGINE-SIDE UNCERTAINTY (flagged for the A/B): the doc confirms VOLT is
 *     additive state; the per-frame interaction when the experiment writes
 *     velocities must still be validated in Unity 2017.4.
 *   => While the experiment runs, the VOLT module is DISABLED (the converter
 *      maps legacy velocity exclusively into VOLT here; startSpeed is 0), so
 *      the script's per-frame velocity writes are the ONLY velocity influence.
 *      The module is re-asserted disabled every frame (self-healing against
 *      other components rebuilding the module) and the FIRST-captured baseline
 *      is restored on switch-off (immutable baseline, no-accumulation doctrine).
 *
 * INITIAL VELOCITY REQUIREMENT (first test: distribution UNCHANGED):
 *
 *   The initial-velocity model is NOT reimplemented (Stage 5 rejected every
 *   candidate spawn-direction law; spawn modeling is a later stage). Instead
 *   each newly spawned particle receives one uniform sample of the captured
 *   VOLT constant-family axes (Constant -> its value; TwoConstants ->
 *   Random.Range(min, max)), i.e. exactly the velocity distribution the
 *   conversion delivers while OFF. New particles are identified by
 *   Particle.randomSeed (uint, 2017.4 API, doc-verified): a seed seen for the
 *   first time is a fresh spawn and gets a baseline sample; seeds that have
 *   left the alive set are pruned. Particles alive at takeover are added to
 *   the seen set WITHOUT any velocity rewrite (they simply start rotating
 *   mid-flight).
 *
 *   FRAMING NOTE (doc-verified ordering): a fresh spawn is first seen in the
 *   LateUpdate AFTER the simulation step that emitted it, so its very first
 *   sim step moves it with startSpeed (0 local units) before the baseline
 *   sample lands. One-frame launch stall of ~|v0|/frameRate local units at
 *   spawn; recorded as an approximation and checkable in the A/B.
 *
 * PRESERVATION / REVERSIBILITY (requirements 1, 2, 3, 9, 10):
 *
 *   'enableVelocityRotation' ships OFF: the component is inert and the system
 *   runs exactly the Stage 1+2 conversion. Toggling ON captures the VOLT
 *   enabled flag + axis baselines on the FIRST takeover (immutable), disables
 *   the module, runs, and toggling OFF (or disabling the component / leaving
 *   Play) restores that captured state byte-for-byte. Lifetime/size/color/
 *   emission/texture-sheet are never touched (no Emit call at all - VOLT
 *   remains the module that delivered every non-rotation quantity).
 *   Restored behavior is exact for all NEW spawns; particles alive across a
 *   restore keep their rotated velocities until they die (finite remaining
 *   lifetime, 3..6 s for the reference system) - VOLT resumes contributing on
 *   top of the carried velocity when re-enabled; no velocity migration is
 *   attempted.
 *
 * INTERPLAY (requirement 4 - warn, never auto-modify other components):
 *
 *   - LegacyParticleOrbit with enableOrbitalSimulation ON: both move particles;
 *     do not run them simultaneously in a comparison (runtime warning).
 *   - LegacyParticleTangentEmitter with enableTangentEmission ON: that
 *     experiment disables the emission module and REWRITES VOLT every frame;
 *     the two recoveries would fight over the same module (runtime warning).
 *   - LegacyParticleVelocityMultiplier present: while the rotation experiment
 *     is ON, spawn velocities are sampled from THIS component's own captured
 *     baseline (first takeover), so multiplier Inspector edits do not change
 *     spawn velocities mid-experiment; the multiplier also keeps rebuilding
 *     VOLT values from its baseline, which is harmless because the module is
 *     re-asserted disabled every frame (runtime warning at takeover).
 *
 * KNOWN APPROXIMATIONS (never hidden):
 *   - Measured on Cloud_bad only; the law form (axis x v) is assumed generic,
 *     the axis value is per-system captured from the preserved ParticleAnimator.
 *   - Per-frame quaternion step = exact rotation of v about the axis; legacy
 *     integrates the same law per-step Euler (difference inside the measured
 *     1.35e-4/step fit error, not additive here because the rotation is exact).
 *   - Seed-based spawn detection is exact for identity (randomSeed is engine-
 *     assigned), but spawn velocity assignment lands one sim step after birth.
 *   - World-space simulation is deliberately NOT supported (attach refuses);
 *     all current rotation-bearing systems (Cloud, Cloud_bad) are Local sim.
 */

/// <summary>
/// Experimental legacy localRotationAxis velocity rotation for V2-converted
/// systems. Attached ONLY to [PSV2] holders via "Tools/Particle System/
/// Experiments/Attach Velocity Rotation (selection)". Defaults OFF = exactly the Stage 1+2
/// conversion output.
/// </summary>
[AddComponentMenu("Legacy Particles V2/Legacy Particle Velocity Rotation")]
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public class LegacyParticleVelocityRotation : MonoBehaviour
{
	[Header("Experiment switch (no recompile)")]
	[Tooltip("Runs the measured legacy velocity rotation dv/dt = localRotationAxis x velocity (quaternion step per frame, Velocity over Lifetime disabled while ON to prevent double-apply). OFF = exact Stage 1+2 conversion behavior (VOLT module untouched, emission untouched).")]
	public bool enableVelocityRotation = false;

	[Header("Captured legacy emitter data (read-only copy of the preserved ParticleAnimator)")]
	[Tooltip("Legacy ParticleAnimator localRotationAxis, UNNORMALIZED: |axis| = rotation rate (rad/s). Stage 5 measured rate <= |a| with omega = |a| * sin(angle(v0, a)).")]
	public Vector3 localRotationAxis = Vector3.zero;

	[System.Serializable]
	public struct AxisBaseline
	{
		public int mode;              // ParticleSystemCurveMode as int
		public float constant;
		public float constantMin;
		public float constantMax;
		public float curveMultiplier; // Curve and TwoCurves modes (2017.4 API name)
		public AnimationCurve curve;
		public AnimationCurve curveMin;
		public AnimationCurve curveMax;
	}

	[Header("Captured conversion module state (restored when the experiment stops)")]
	[SerializeField]
	bool stateCaptured = false;

	[SerializeField]
	AxisBaseline baselineX;

	[SerializeField]
	AxisBaseline baselineY;

	[SerializeField]
	AxisBaseline baselineZ;

	[SerializeField]
	bool voltWasEnabled = false;

	[SerializeField]
	bool experimentRunning = false;

	ParticleSystem m_ParticleSystem;
	ParticleSystem.Particle[] m_Buffer;
	System.Collections.Generic.HashSet<uint> seenSeeds;
	bool interplayWarned = false;

	const float kZeroLength = 1e-6f;

	void Awake()
	{
		m_ParticleSystem = GetComponent<ParticleSystem>();
	}

	void OnEnable()
	{
		if (m_ParticleSystem == null)
			m_ParticleSystem = GetComponent<ParticleSystem>();

		if (enableVelocityRotation && !experimentRunning && stateCaptured)
			TakeOver();
	}

	void OnDisable()
	{
		if (experimentRunning)
			Restore();
	}

	void LateUpdate()
	{
		if (m_ParticleSystem == null)
			m_ParticleSystem = GetComponent<ParticleSystem>();

		// Inspector-toggle state machine (also covers Play begin/end transitions).
		if (enableVelocityRotation && !experimentRunning && stateCaptured)
			TakeOver();
		else if (!enableVelocityRotation && experimentRunning)
			Restore();

		if (!experimentRunning)
			return;

		float dt = Time.deltaTime;
		if (dt <= 0f)
			return;

		// Re-assert VOLT disabled every frame: keeps the experiment self-healing
		// if another component (e.g. the Stage-3b velocity multiplier rebuild in
		// its OnValidate) touches the module while this experiment runs.
		ParticleSystem.VelocityOverLifetimeModule velocityModule = m_ParticleSystem.velocityOverLifetime;
		if (velocityModule.enabled)
			velocityModule.enabled = false;

		// One-time interplay warnings (requirement 4): warn, never auto-modify.
		if (!interplayWarned)
		{
			interplayWarned = true;
			LegacyParticleOrbit orbit = GetComponent<LegacyParticleOrbit>();
			if (orbit != null && orbit.enableOrbitalSimulation)
				UnityEngine.Debug.LogWarning("LegacyParticleVelocityRotation on " 
					+ GetHolderName() + ": a LegacyParticleOrbit with enableOrbitalSimulation=true is ALSO active. Requirement 4: do not run Stage-2 positional orbit and Stage-6 velocity rotation simultaneously in a comparison (one knob at a time).");
			LegacyParticleTangentEmitter tangentEmitter = GetComponent<LegacyParticleTangentEmitter>();
			if (tangentEmitter != null && tangentEmitter.enableTangentEmission)
				UnityEngine.Debug.LogWarning("LegacyParticleVelocityRotation on " 
					+ GetHolderName() + ": a LegacyParticleTangentEmitter with enableTangentEmission=true is ALSO active. Both experiments rewrite/disable the same Velocity over Lifetime module - switch one off before comparing.");
			if (GetComponent<LegacyParticleVelocityMultiplier>() != null)
				UnityEngine.Debug.LogWarning("LegacyParticleVelocityRotation on " 
					+ GetHolderName() + ": a LegacyParticleVelocityMultiplier is attached. While the rotation experiment is ON, spawn velocities are sampled from the rotation component's own captured baseline, so multiplier edits do not change spawn velocities mid-experiment; multiplier VOLT rebuilds are inert here (module re-asserted disabled every frame).");
		}

		ParticleSystemsRotationStep(dt);
	}

	/// <summary>
	/// Per-frame step (requirements 5-8): seed-based fresh-spawn detection with
	/// baseline-sample assignment, then one quaternion velocity rotation applied
	/// to every particle, then full-buffer write-back.
	/// </summary>
	void ParticleSystemsRotationStep(float dt)
	{
		if (seenSeeds == null)
			return; // unreachable while experimentRunning; defensive

		// 2017.4: GetParticles does not enlarge the array by itself - ensure
		// capacity first (burst overage safe), then read the full alive buffer.
		int wanted = Mathf.Max(m_ParticleSystem.particleCount, 1);
		if (m_Buffer == null || m_Buffer.Length < wanted)
			m_Buffer = new ParticleSystem.Particle[wanted];
		int count = m_ParticleSystem.GetParticles(m_Buffer);
		if (count == 0)
		{
			seenSeeds.Clear();
			return;
		}

		// axis: UNNORMALIZED magnitude drives the rate, normalized direction drives the plane.
		float axisRate = localRotationAxis.magnitude;
		Vector3 axisDir = localRotationAxis;
		if (axisRate > kZeroLength)
			axisDir /= axisRate;
		else
			axisRate = 0f; // captured as zero? attach preconditions forbid; inert instead of guessing

		if (axisRate <= kZeroLength)
			return; // nothing to apply; restores are untouched

		Quaternion step = Quaternion.AngleAxis(axisRate * dt * Mathf.Rad2Deg, axisDir);

		// Fresh-spawn detection + pruning over the CURRENT alive seed set.
		// Duplicate seeds within one buffer are not expected (engine-unique); a
		// seed re-entering the alive set is treated as the same particle identity.
		System.Collections.Generic.HashSet<uint> assignedThisFrame = null;
		for (int i = 0; i < count; i++)
		{
			uint seed = m_Buffer[i].randomSeed;
			if (!seenSeeds.Contains(seed))
			{
				// Fresh spawn: assign ONE uniform sample of the captured VOLT
				// baselines (initial-velocity distribution preserved; the VOLT
				// module itself is disabled so it cannot add on top).
				Vector3 v = new Vector3(
					SampleAxisValue(baselineX),
					SampleAxisValue(baselineY),
					SampleAxisValue(baselineZ));

				ParticleSystem.Particle p = m_Buffer[i];
				p.velocity = v;
				m_Buffer[i] = p;

				seenSeeds.Add(seed);
				if (assignedThisFrame == null)
					assignedThisFrame = new System.Collections.Generic.HashSet<uint>();
				assignedThisFrame.Add(seed);
				// The JUST-ASSIGNED spawn velocity is NOT rotated below - legacy
				// integrates one raw step with v0 first; rotation applies on the
				// subsequent frames like for every other particle.
			}
		}

		// Rotate carried velocities: dv/dt = a x v via the exact quaternion step.
		for (int i = 0; i < count; i++)
		{
			if (assignedThisFrame != null && assignedThisFrame.Contains(m_Buffer[i].randomSeed))
				continue; // assignment frame: move one raw step first

			ParticleSystem.Particle p = m_Buffer[i];
			p.velocity = step * p.velocity;
			m_Buffer[i] = p;
		}

		m_ParticleSystem.SetParticles(m_Buffer, count);

		// Prune seeds of particles that died this frame (keep the set small on
		// long-running scenes; alive set is small by construction here).
		if (seenSeeds.Count > count)
		{
			System.Collections.Generic.HashSet<uint> alive = new System.Collections.Generic.HashSet<uint>();
			for (int i = 0; i < count; i++)
				alive.Add(m_Buffer[i].randomSeed);
			seenSeeds.RemoveWhere(delegate (uint s) { return !alive.Contains(s); });
		}
	}

	/// <summary>One uniform sample of a captured VOLT axis baseline (Constant / TwoConstants only; curve modes are refused at attach and takeover).</summary>
	static float SampleAxisValue(AxisBaseline baseline)
	{
		switch ((ParticleSystemCurveMode)baseline.mode)
		{
			case ParticleSystemCurveMode.Constant:
				return baseline.constant;
			case ParticleSystemCurveMode.TwoConstants:
				return Random.Range(baseline.constantMin, baseline.constantMax);
			default:
				return 0f; // refused at attach; never guesses
		}
	}

	/// <summary>
	/// One-time capture on the FIRST takeover (immutable baseline): VOLT enabled
	/// flag + axis baselines. Then disables the VOLT module and seeds the seen
	/// set with currently alive particles WITHOUT rewriting their velocities
	/// (mid-flight particles simply start rotating).
	/// </summary>
	void TakeOver()
	{
		ParticleSystem.MainModule main = m_ParticleSystem.main;
		if (main.simulationSpace != ParticleSystemSimulationSpace.Local)
		{
			// attach preconditions should make this unreachable; refuse silently
			// rather than guess about velocity frames.
			return;
		}

		if (!stateCaptured)
		{
			ParticleSystem.VelocityOverLifetimeModule velocityModule = m_ParticleSystem.velocityOverLifetime;
			if (!velocityModule.enabled)
				return; // nothing captured = no spawn distribution; never guess (off -> restore no-op)
			voltWasEnabled = velocityModule.enabled;
			baselineX = CaptureAxis(velocityModule.x);
			baselineY = CaptureAxis(velocityModule.y);
			baselineZ = CaptureAxis(velocityModule.z);
			stateCaptured = true;
		}

		// Seen-set seeding (also on LATER takeovers: identities are re-learned,
		// but velocity assignment only ever happens for truly fresh spawns).
		seenSeeds = new System.Collections.Generic.HashSet<uint>();
		int wanted = Mathf.Max(m_ParticleSystem.particleCount, 1);
		if (m_Buffer == null || m_Buffer.Length < wanted)
			m_Buffer = new ParticleSystem.Particle[wanted];
		int count = m_ParticleSystem.GetParticles(m_Buffer);
		for (int i = 0; i < count; i++)
			seenSeeds.Add(m_Buffer[i].randomSeed);

		ParticleSystem.VelocityOverLifetimeModule voltNow = m_ParticleSystem.velocityOverLifetime;
		voltNow.enabled = false;

		experimentRunning = true;
	}

	/// <summary>
	/// Restores the captured VOLT state (axes + enabled flag) - equivalent to the
	/// Stage 1+2 conversion output (which is what the Stage-3b multiplier baseline
	/// also reproduces).
	/// </summary>
	void Restore()
	{
		ParticleSystem.VelocityOverLifetimeModule velocityModule = m_ParticleSystem.velocityOverLifetime;

		// 2017.4 API: property accesses cannot be passed by ref (CS0206) -
		// copy each MinMaxCurve axis into a local, mutate the copy, write back.
		ParticleSystem.MinMaxCurve vx = velocityModule.x;
		RestoreAxis(ref vx, baselineX);
		velocityModule.x = vx;

		ParticleSystem.MinMaxCurve vy = velocityModule.y;
		RestoreAxis(ref vy, baselineY);
		velocityModule.y = vy;

		ParticleSystem.MinMaxCurve vz = velocityModule.z;
		RestoreAxis(ref vz, baselineZ);
		velocityModule.z = vz;

		velocityModule.enabled = voltWasEnabled;

		seenSeeds = null; // re-learned identities on the next takeover
		experimentRunning = false;
	}

	string GetHolderName()
	{
		return transform.name;
	}

	static AxisBaseline CaptureAxis(ParticleSystem.MinMaxCurve axis)
	{
		AxisBaseline baseline = default(AxisBaseline);
		baseline.mode = (int)axis.mode;
		switch (axis.mode)
		{
			case ParticleSystemCurveMode.Constant:
				baseline.constant = axis.constant;
				break;
			case ParticleSystemCurveMode.TwoConstants:
				baseline.constantMin = axis.constantMin;
				baseline.constantMax = axis.constantMax;
				break;
			case ParticleSystemCurveMode.Curve:
				baseline.curveMultiplier = axis.curveMultiplier;
				baseline.curve = axis.curve;
				break;
			case ParticleSystemCurveMode.TwoCurves:
				baseline.curveMultiplier = axis.curveMultiplier;
				baseline.curveMin = axis.curveMin;
				baseline.curveMax = axis.curveMax;
				break;
		}
		return baseline;
	}

	/// <summary>Exact baseline restore; curve assignments first, curveMultiplier last (setters may reset the multiplier).</summary>
	static void RestoreAxis(ref ParticleSystem.MinMaxCurve axis, AxisBaseline baseline)
	{
		switch ((ParticleSystemCurveMode)baseline.mode)
		{
			case ParticleSystemCurveMode.Constant:
				axis.constant = baseline.constant;
				break;
			case ParticleSystemCurveMode.TwoConstants:
				axis.constantMin = baseline.constantMin;
				axis.constantMax = baseline.constantMax;
				break;
			case ParticleSystemCurveMode.Curve:
				axis.curve = baseline.curve;
				axis.curveMultiplier = baseline.curveMultiplier;
				break;
			case ParticleSystemCurveMode.TwoCurves:
				axis.curveMin = baseline.curveMin;
				axis.curveMax = baseline.curveMax;
				axis.curveMultiplier = baseline.curveMultiplier;
				break;
		}
	}
}
