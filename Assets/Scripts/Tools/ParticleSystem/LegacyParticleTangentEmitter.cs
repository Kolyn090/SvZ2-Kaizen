using UnityEngine;

/*
 * LegacyParticleTangentEmitter (runtime component, created by LegacyParticleUpdaterV2)
 *
 * STAGE 4 - optional, reversible experiment: reconstructs the legacy
 * tangentVelocity emission (spawn-position-coupled tangential velocity) with two
 * candidate models, selected in the Inspector, without recompiling:
 *
 *   - CrossProduct:    v = n x t         (|v| = |t| * sin(angle(n, t)))
 *   - ProjectedField:  v = t - n(n . t)  (identical magnitude distribution,
 *                        different angular pattern)
 *
 *   n = normalized spawn position relative to the emitter center (the manual's
 *   "starting speed across the Emitter's surface"), t = legacy tangentVelocity.
 *   BOTH are READINGS of undocumented legacy behavior, not known facts - the
 *   model A/B is exactly what this experiment is for. The radial component is
 *   structurally zero in both models (that part is model-independent).
 *
 * HOW INITIAL VELOCITY IS ASSIGNED (Unity 2017.4 API, verified against the
 * 2017.4 ScriptReference before implementation):
 *
 *   ParticleSystem.Emit(ParticleSystem.EmitParams emitParams, int count)
 *   - "Setting properties in the emitParams will override those properties in
 *     the emitted particles. Any properties not modified will inherit the
 *     behavior specified in the inspector."
 *   => ONLY position and velocity are set on EmitParams. startLifetime,
 *      startSize, startColor, startRotation and everything else keep being
 *      delivered by the system's own modules (requirement 7: emission timing,
 *      lifetime, size, color, material and texture sheet animation are NOT
 *      reimplemented or duplicated). No GetParticles/SetParticles is used.
 *
 * HOW DOUBLE-APPLICATION IS PREVENTED (requirement 6):
 *
 *   While the experiment runs, two things happen once at takeover:
 *     1. the Emission module is DISABLED (script does the emission), and
 *     2. the Velocity over Lifetime envelope is REWRITTEN WITHOUT the tangent
 *        contribution (center +/- |rndVelocity| only; tangentVelocity is
 *        removed from the per-axis envelope).
 *   VOLT then contributes exactly the non-tangent legacy velocity parts
 *   (localVelocity/worldVelocity center + rndVelocity randomness) while the
 *   script injects only the tangent part. The without-tangent VOLT is
 *   re-asserted every frame, so the LegacyParticleVelocityMultiplier
 *   component's baseline rebuilds (its OnValidate) cannot silently re-add the
 *   tangent envelope while the experiment is on. INTERPLAY CAVEAT (documented,
 *   choose one knob at a time): with the experiment ON, the Stage-3b velocity
 *   multiplier governs only the non-tangent VOLT part; the tangent part has
 *   its own `velocityMagnitudeScale`.
 *
 * REVERSIBILITY (requirement 9):
 *
 *   `enableTangentEmission` starts OFF: the component is inert and the system
 *   runs exactly the Stage 1+2 conversion. Toggling it ON runs the experiment;
 *   toggling OFF (or disabling the component / exiting Play) restores the
 *   captured Emission module state (enabled flag + rate values) and the exact
 *   captured VOLT state - the state captured at the FIRST takeover is used for
 *   every restore (immutable baseline semantics, matching the project's
 *   no-accumulation doctrine).
 *
 * KNOWN APPROXIMATIONS (never hidden):
 *
 *   - Emission rate: legacy re-rolls min..max emission "per emission event";
 *     this script re-rolls once per SECOND (closest continuous equivalent of
 *     the V2 conversion's per-event reading). Rate grain is an approximation
 *     of both (recorded in the conversion record).
 *   - Every legacy velocity contribution is summed AT SPAWN; VOLT applies the
 *     non-tangent part over the whole particle life. With damping = 1 (the
 *     experiment's attach precondition) speed is constant, so the phase
 *     difference is invisible; damping != 1 systems are refused at attach for
 *     that reason.
 *   - Uniform ellipsoid sampling: uniform unit ball (r = cbrt(u)) scaled by
 *     the extents; negative extents take the r = 1 surface path (spawn ON the
 *     ellipsoid surface, sign of extents flips the axis sample).
 *   - MinEmitterRange: legacy relative minimum spawn distance, implemented as
 *     the minimum unit-ball radius before extents scaling (project data uses 0,
 *     i.e. inactive; verify visually if a future system uses nonzero values).
 *   - Cross-model sign (n x t vs t x n) is undocumented; pick the model whose
 *     swirl direction matches the legacy original.
 *   - World-space simulation is deliberately NOT supported (attach refuses):
 *     the tangential frame mapping would re-open the disproven Stage-3 scale
 *     question. All current tangent systems (Cloud, Cloud_bad) are Local sim.
 */

/// <summary>
/// Experimental legacy tangentVelocity reconstruction for V2-converted systems.
/// Attached ONLY to [PSV2] holders via "Tools/Particle System/Experiments/Attach
/// Tangent Emitter (selection)". Defaults OFF = exactly the Stage 1+2 conversion output.
/// </summary>
[AddComponentMenu("Legacy Particles V2/Legacy Particle Tangent Emitter")]
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public class LegacyParticleTangentEmitter : MonoBehaviour
{
	/// <summary>Candidate tangent-direction models (Inspector-selectable, no recompile).</summary>
	public enum TangentModel
	{
		CrossProduct = 0,   // v = n x t
		ProjectedField = 1, // v = t - n(n . t)
	}

	[Header("Experiment switch + models (all in this Inspector, no recompile)")]
	[Tooltip("Runs the experiment: emission module off, script emits with per-particle tangent velocity computed from the spawn position. OFF = exact Stage 1+2 conversion behavior (emission module + Velocity over Lifetime untouched).")]
	public bool enableTangentEmission = false;

	[Tooltip("CrossProduct: v = n x t (whirl around the (1,1,1) axis of t). ProjectedField: v = t - n(n . t) (sliding field with poles). Pick whichever matches the legacy original in the A/B.")]
	public TangentModel tangentModel = TangentModel.CrossProduct;

	[Tooltip("Scales the TANGENT velocity magnitude only (direction unchanged; 1.0 = authored magnitude). Independent from the LegacyParticleVelocityMultiplier component, which governs the non-tangent Velocity over Lifetime part while this experiment is on.")]
	public float velocityMagnitudeScale = 1f;

	[Header("Captured legacy emitter data (read-only copies of the preserved components)")]
	public Vector3 tangentVelocity = Vector3.zero;
	public Vector3 localVelocity = Vector3.zero;
	public Vector3 worldVelocity = Vector3.zero;
	public Vector3 rndVelocity = Vector3.zero;
	public Vector3 ellipsoid = Vector3.one * 0.12f;
	public float minEmitterRange = 0f;
	public float minEmission = 1f;
	public float maxEmission = 4f;

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
	bool emissionWasEnabled = true;

	[SerializeField]
	int savedRateMode;

	[SerializeField]
	float savedRateMin;

	[SerializeField]
	float savedRateMax;

	[SerializeField]
	bool experimentRunning = false;

	ParticleSystem m_ParticleSystem;
	float rateTimer;
	float emitAccumulator;
	float currentRate;

	const float kZeroLength = 1e-6f;

	void Awake()
	{
		m_ParticleSystem = GetComponent<ParticleSystem>();
	}

	void OnEnable()
	{
		if (m_ParticleSystem == null)
			m_ParticleSystem = GetComponent<ParticleSystem>();

		if (enableTangentEmission && !experimentRunning && stateCaptured)
			TakeOver();
	}

	void OnDisable()
	{
		if (experimentRunning)
			Restore();
	}

	void Update()
	{
		if (m_ParticleSystem == null)
			m_ParticleSystem = GetComponent<ParticleSystem>();

		// Inspector-toggle state machine (also covers Play begin/end transitions).
		if (enableTangentEmission && !experimentRunning && stateCaptured)
			TakeOver();
		else if (!enableTangentEmission && experimentRunning)
			Restore();

		if (!experimentRunning)
			return;

		{
			// Re-assert the without-tangent VOLT every frame: keeps the experiment
			// self-healing if another component (e.g. the Stage-3b velocity
			// multiplier) rebuilds the module from ITS baseline (which is the
			// with-tangent conversion state) while this experiment runs.
			ParticleSystem.VelocityOverLifetimeModule velocity = m_ParticleSystem.velocityOverLifetime;
			Vector3 centerEmitterLocal = localVelocity;
			if (worldVelocity.sqrMagnitude > Mathf.Epsilon)
				centerEmitterLocal += transform.InverseTransformDirection(worldVelocity); // rotation only, as the converter composes it

			Vector3 envelope = new Vector3(
				Mathf.Abs(rndVelocity.x),
				Mathf.Abs(rndVelocity.y),
				Mathf.Abs(rndVelocity.z));

			// Property accesses cannot be passed by ref (CS0206): copy each MinMaxCurve
			// axis into a local, mutate the copy, write it back to the module property.
			ParticleSystem.MinMaxCurve vx = velocity.x;
			ParticleSystem.MinMaxCurve vy = velocity.y;
			ParticleSystem.MinMaxCurve vz = velocity.z;

			SetAxisFromRange(ref vx, centerEmitterLocal.x - envelope.x, centerEmitterLocal.x + envelope.x);
			SetAxisFromRange(ref vy, centerEmitterLocal.y - envelope.y, centerEmitterLocal.y + envelope.y);
			SetAxisFromRange(ref vz, centerEmitterLocal.z - envelope.z, centerEmitterLocal.z + envelope.z);
			velocity.x = vx;
			velocity.y = vy;
			velocity.z = vz;
		}

		float dt = Time.deltaTime;
		if (dt <= 0f)
			return;

		// Emission timing: re-roll the rate once per second (approximation of legacy
		// min..max re-rolling), accumulate fractional particles, emit whole ones.
		rateTimer += dt;
		while (rateTimer >= 1f)
		{
			rateTimer -= 1f;
			currentRate = Random.Range(minEmission, maxEmission);
		}

		emitAccumulator += currentRate * dt;
		while (emitAccumulator >= 1f)
		{
			emitAccumulator -= 1f;
			EmitOne();
		}
	}

	/// <summary>
	/// One-time capture (first takeover is the immutable baseline): Emission module
	/// state and the full VOLT state. Then disables script-driven emission and
	/// rewrites VOLT without the tangent envelope.
	/// </summary>
	void TakeOver()
	{
		ParticleSystem.EmissionModule emission = m_ParticleSystem.emission;

		if (!stateCaptured)
		{
			emissionWasEnabled = emission.enabled;

			ParticleSystem.MinMaxCurve rate = emission.rateOverTime;
			if (rate.mode != ParticleSystemCurveMode.Constant && rate.mode != ParticleSystemCurveMode.TwoConstants)
				return; // never guess: attach preconditions should make this unreachable
			savedRateMode = (int)rate.mode;
			savedRateMin = rate.mode == ParticleSystemCurveMode.Constant ? rate.constant : rate.constantMin;
			savedRateMax = rate.mode == ParticleSystemCurveMode.Constant ? rate.constant : rate.constantMax;

			ParticleSystem.VelocityOverLifetimeModule velocity = m_ParticleSystem.velocityOverLifetime;
			baselineX = CaptureAxis(velocity.x);
			baselineY = CaptureAxis(velocity.y);
			baselineZ = CaptureAxis(velocity.z);
			stateCaptured = true;
		}

		ParticleSystem.EmissionModule emissionNow = m_ParticleSystem.emission;
		emissionNow.enabled = false;

		experimentRunning = true;
		rateTimer = 0f;
		emitAccumulator = 0f;
		currentRate = Random.Range(minEmission, maxEmission);
	}

	/// <summary>
	/// Restores the captured Emission module (enabled flag + rate values) and the
	/// exact captured VOLT state - equivalent to the Stage 1+2 conversion output
	/// (which is what the stage-3b multiplier baseline also reproduces).
	/// </summary>
	void Restore()
	{
		ParticleSystem.EmissionModule emission = m_ParticleSystem.emission;
		emission.enabled = emissionWasEnabled;

		ParticleSystem.MinMaxCurve rate = emission.rateOverTime;
		switch ((ParticleSystemCurveMode)savedRateMode)
		{
			case ParticleSystemCurveMode.Constant:
				rate.constant = savedRateMin;
				break;
			case ParticleSystemCurveMode.TwoConstants:
				rate.constantMin = savedRateMin;
				rate.constantMax = savedRateMax;
				break;
		}
		emission.rateOverTime = rate;

		ParticleSystem.VelocityOverLifetimeModule velocity = m_ParticleSystem.velocityOverLifetime;
		ParticleSystem.MinMaxCurve vx = velocity.x;
		RestoreAxis(ref vx, baselineX);
		velocity.x = vx;

		ParticleSystem.MinMaxCurve vy = velocity.y;
		RestoreAxis(ref vy, baselineY);
		velocity.y = vy;

		ParticleSystem.MinMaxCurve vz = velocity.z;
		RestoreAxis(ref vz, baselineZ);
		velocity.z = vz;

		experimentRunning = false;
	}

	/// <summary>
	/// Uniform sample inside the legacy ellipsoid (uniform unit ball r = cbrt(u)
	/// then per-axis extents scaling; linear maps preserve uniformity). Negative
	/// extents: legacy emits from the surface - r = 1 path with extents signs
	/// applied componentwise. MinEmitterRange = legacy relative minimum spawn
	/// distance, applied as the minimum unit-ball radius BEFORE extents scaling.
	/// </summary>
	Vector3 SampleSpawnPoint()
	{
		Vector3 dir = Random.onUnitSphere;
		Vector3 sample;

		bool surfaceEmission = ellipsoid.x < 0f || ellipsoid.y < 0f || ellipsoid.z < 0f;
		if (surfaceEmission)
		{
			sample = new Vector3(dir.x * ellipsoid.x, dir.y * ellipsoid.y, dir.z * ellipsoid.z);
		}
		else
		{
			float minR = Mathf.Clamp(minEmitterRange, 0f, 0.999f);
			float cube = Random.value * (1f - minR * minR * minR) + minR * minR * minR;
			float r = Mathf.Pow(cube, 1f / 3f); // (Mathf.Cbrt does not exist in the 2017.4 API)
			sample = new Vector3(dir.x * r * ellipsoid.x, dir.y * r * ellipsoid.y, dir.z * r * ellipsoid.z);
		}
		return sample;
	}

	/// <summary>
	/// Tangent velocity from the spawn position (requirement 4) with the selected
	/// model. Magnitude = authored |tangentVelocity| geometry in both models
	/// (|n x t| = |t| sin(angle) and |t - n(n.t)|); velocityMagnitudeScale scales
	/// ONLY the magnitude (requirement 5), direction is purely topological.
	/// </summary>
	Vector3 ComputeTangentVelocity(Vector3 spawnPosition)
	{
		Vector3 n = spawnPosition;
		float length = n.magnitude;
		if (length < kZeroLength)
			n = Random.onUnitSphere; // degenerate center spawn: any direction is equally valid
		else
			n /= length;

		Vector3 v;
		switch (tangentModel)
		{
			case TangentModel.CrossProduct:
				// Radial component structurally zero (verified property of the model);
				// magnitude = |t| sin(angle(n, t)) - zero at the (1,1,1) poles of t.
				v = Vector3.Cross(n, tangentVelocity);
				break;
			case TangentModel.ProjectedField:
				v = tangentVelocity - Vector3.Dot(n, tangentVelocity) * n;
				break;
			default:
				v = Vector3.zero;
				break;
		}

		float magnitude = Mathf.Max(0f, velocityMagnitudeScale);
		return v * magnitude;
	}

	void EmitOne()
	{
		// 2017.4 API: "Setting properties in the emitParams will override those
		// properties in the emitted particles. Any properties not modified will
		// inherit the behavior specified in the inspector." Only position and
		// velocity are set; startLifetime/startSize/startColor/startRotation are
		// NOT touched so the system's own modules keep delivering them.
		ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams();
		Vector3 spawnPosition = SampleSpawnPoint();
		emitParams.position = spawnPosition;
		emitParams.velocity = ComputeTangentVelocity(spawnPosition);
		m_ParticleSystem.Emit(emitParams, 1);
	}

	/// <summary>Writes min..max to an axis, mirroring the converter's TwoConstants helper (Constant mode when min == max).</summary>
	static void SetAxisFromRange(ref ParticleSystem.MinMaxCurve axis, float min, float max)
	{
		if (Mathf.Approximately(min, max))
		{
			axis.mode = ParticleSystemCurveMode.Constant;
			axis.constant = min;
		}
		else
		{
			axis.mode = ParticleSystemCurveMode.TwoConstants;
			axis.constantMin = min;
			axis.constantMax = max;
		}
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
