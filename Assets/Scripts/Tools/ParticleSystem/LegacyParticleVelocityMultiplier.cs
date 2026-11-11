using UnityEngine;

/*
 * LegacyParticleVelocityMultiplier (runtime component, created by LegacyParticleUpdaterV2)
 *
 * EXPERIMENTAL visual calibration for the converted particle systems:
 * scales the system's Velocity over Lifetime velocities by a user-editable
 * factor, adjustable live in the Inspector (including Play mode). It is NOT
 * part of the legacy conversion mapping - it is an authorized user-driven
 * calibration knob for the remaining Cloud_bad compactness experiment, audited
 * in the conversion record as experimental calibration, never as an exact
 * mapping, and it must not replace a structural tangent-velocity fix later.
 *
 * HOW IT WORKS (no per-frame code, no GetParticles/SetParticles):
 *
 *   At attach time, 'Attach Velocity Multiplier (selection)' calls
 *   CaptureBaseline() ONCE, storing the exact post-conversion Velocity over
 *   Lifetime component state in serialized fields (per axis: curve mode,
 *   constants, or curves plus the curveMultiplier). OnValidate() then re-writes the
 *   module as baseline * velocityMultiplier every time the user edits the
 *   multiplier.
 *
 *   => REPEATED EDITS NEVER ACCUMULATE (requirement): the module is always
 *      rebuilt from the immutable baseline, so 1.2 -> 1.5 yields exactly 150%
 *      of baseline, and setting 1.0 always returns the exact conversion values
 *      (bit-identical, not just approximately).
 *
 *   => NOTHING BUT THE VELOCITY is scaled (requirement 6): only the VOLT
 *      module's x/y/z components are touched. Emission rate, start size,
 *      start lifetime, the Shape module (spawn ball) and the LegacyParticleOrbit
 *      component's angular speed are never touched by this component.
 *
 * HOW EVERY MAGNITUDE MODE STAYS CORRECT (requirement 5: signs, distributions,
 * curve shapes preserved) - per the Unity 2017.4 MinMaxCurve API (constant,
 * constantMin/Max, curve, curveMin/Max, curveMultiplier):
 *
 *   Curve modes evaluate value(u) = curveMultiplier * curve(u) (normalized keys).
 *   Scaling ONLY the curveMultiplier / constants multiplies the effective output
 *   while leaving curve keys untouched:
 *
 *     - Constant:      constant *= m           (sign kept: negative scales)
 *     - TwoConstants:  constantMin/Max *= m    (uniform distribution stays a
 *                       uniform distribution over proportionally scaled bounds)
 *     - Curve:         curveMultiplier *= m (keys untouched - shape preserved)
 *     - TwoCurves:     curveMultiplier *= m (both curves untouched). This is the
 *                       shape the Stage-1 damping writes (normalized power-law
 *                       curves with a curveMultiplier normalizer), so a
 *                       multiplier composes CORRECTLY with the documented
 *                       damping v(t) = v0 * damping^t: the damping SHAPE is
 *                       untouched.
 *
 * KNOWN LIMITATIONS (deliberately documented, not hidden):
 *
 *   - Baseline capture: the baseline is whatever the module held when the
 *     component was attached (normally the fresh V2 conversion output). If the
 *     module state was manually tweaked before attaching, those tweaks become
 *     "baseline". Reverting + re-converting the system deletes this component
 *     with the holder; after reconversion it must be re-attached (then the
 *     baseline is the new conversion output). Manual VOLT edits made AFTER
 *     attaching are overwritten on the next multiplier edit - the baseline wins
 *     (that is the accumulation guard working as designed).
 *   - OnValidate runs only in the EDITOR (both Edit and Play mode). In a
 *     player build the component is inert: bake the final multiplier by
 *     setting it and saving the scene (the module then carries the scaled
 *     values inside the scene asset).
 *   - Undo: attaching the component is Undo-registered (Undo.AddComponent).
 *     Within one session, editing the multiplier back to 1.0 restores the
 *     exact baseline module values; Ctrl+Z of the multiplier field itself
 *     also re-applies consistently (OnValidate re-fires on the undo).
 */

/// <summary>
/// Experimental Velocity over Lifetime calibration knob for V2-converted systems.
/// Attached ONLY to [PSV2] holders via "Tools/Particle System/Experiments/Attach
/// Velocity Multiplier (selection)". Default 1.0 = exactly the conversion baseline.
/// </summary>
[AddComponentMenu("Legacy Particles V2/Legacy Particle Velocity Multiplier")]
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public class LegacyParticleVelocityMultiplier : MonoBehaviour
{
	/// <summary>Serialized snapshot of one Velocity over Lifetime axis.</summary>
	[System.Serializable]
	public struct AxisBaseline
	{
		public int mode;          // ParticleSystemCurveMode as int (enum types serialize poorly in structs in 2017.4)
		public float constant;
		public float constantMin;
		public float constantMax;
		public float curveMultiplier; // curve magnitude for Curve and TwoCurves modes (2017.4 has no 'scalar' member)
		public AnimationCurve curve;    // Curve mode
		public AnimationCurve curveMin; // TwoCurves mode
		public AnimationCurve curveMax; // TwoCurves mode
	}

	[Header("Calibration (all in this Inspector, no recompile)")]
	[Tooltip("Scales the converted system's Velocity over Lifetime velocities. 1.0 = conversion baseline (unchanged Stage 1/2 output). 1.2 = 120%, 1.5 = 150%. Applies immediately in the Inspector, live during Play mode for A/B testing. EXPERIMENTAL VISUAL CALIBRATION - not a legacy value.")]
	public float velocityMultiplier = 1f;

	[Tooltip("Multiplier currently applied to the module (informational; kept in sync automatically).")]
	[SerializeField]
	float appliedMultiplier = 1f;

	[Tooltip("Baseline Velocity over Lifetime state captured at attach time (multiplier = 1). Every change recomputes from this baseline, so repeated edits never accumulate.")]
	[SerializeField]
	bool baselineCaptured = false;

	[SerializeField]
	AxisBaseline baselineX;

	[SerializeField]
	AxisBaseline baselineY;

	[SerializeField]
	AxisBaseline baselineZ;

	ParticleSystem m_ParticleSystem;

	void Awake()
	{
		m_ParticleSystem = GetComponent<ParticleSystem>();
	}

	/// <summary>
	/// Captures the CURRENT Velocity over Lifetime state as the immutable baseline
	/// (equivalent to multiplier = 1). Called ONCE by the editor attach menu;
	/// capture again only if the conversion itself was redone.
	/// </summary>
	public void CaptureBaseline(ParticleSystem ps)
	{
		m_ParticleSystem = ps != null ? ps : GetComponent<ParticleSystem>();
		ParticleSystem.VelocityOverLifetimeModule velocity = m_ParticleSystem.velocityOverLifetime;
		baselineX = CaptureAxis(velocity.x);
		baselineY = CaptureAxis(velocity.y);
		baselineZ = CaptureAxis(velocity.z);
		baselineCaptured = true;
	}

	/// <summary>
	/// Manual re-apply (also runs automatically whenever any serialized value of
	/// this component changes). Rebuilds every axis from baseline * multiplier.
	/// </summary>
	void OnValidate()
	{
		if (!baselineCaptured)
			return; // baseline not captured yet (component freshly added): never guess

		Apply(velocityMultiplier);
	}

	void Apply(float multiplier)
	{
		if (m_ParticleSystem == null)
			m_ParticleSystem = GetComponent<ParticleSystem>();

		ParticleSystem.VelocityOverLifetimeModule velocity = m_ParticleSystem.velocityOverLifetime;
		float m = Mathf.Max(0f, multiplier); // clamps the knob itself; the sign of the velocity values is preserved

		ParticleSystem.MinMaxCurve vx = velocity.x;
		RestoreAndScale(ref vx, baselineX, m);
		velocity.x = vx;

		ParticleSystem.MinMaxCurve vy = velocity.y;
		RestoreAndScale(ref vy, baselineY, m);
		velocity.y = vy;

		ParticleSystem.MinMaxCurve vz = velocity.z;
		RestoreAndScale(ref vz, baselineZ, m);
		velocity.z = vz;

		appliedMultiplier = m;
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

	/// <summary>
	/// Baseline restore (exact conversion output) followed by a pure magnitude
	/// scale. Curve keys are NEVER modified - only the scalar (and constants) -
	/// so signs, uniform-distribution bounds and curve shapes all survive
	/// (see the class header for the per-mode math).
	/// </summary>
	static void RestoreAndScale(ref ParticleSystem.MinMaxCurve axis, AxisBaseline baseline, float m)
	{
		switch ((ParticleSystemCurveMode)baseline.mode)
		{
			case ParticleSystemCurveMode.Constant:
				axis.constant = baseline.constant * m;
				break;
			case ParticleSystemCurveMode.TwoConstants:
				axis.constantMin = baseline.constantMin * m;
				axis.constantMax = baseline.constantMax * m;
				break;
			case ParticleSystemCurveMode.Curve:
				// Curve key data FIRST, curveMultiplier LAST: a curve property setter may
				// reset curveMultiplier to 1, so the multiplied value must be assigned
				// after every curve in the same restore step.
				axis.curve = baseline.curve;
				axis.curveMultiplier = baseline.curveMultiplier * m;
				break;
			case ParticleSystemCurveMode.TwoCurves:
				axis.curveMin = baseline.curveMin;
				axis.curveMax = baseline.curveMax;
				axis.curveMultiplier = baseline.curveMultiplier * m; // AFTER the curve sets (see note above)
				break;
		}
	}
}
