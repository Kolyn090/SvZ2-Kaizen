using UnityEngine;

/*
 * LegacyParticleOrbit (runtime component, created by LegacyParticleUpdaterV2)
 *
 * Reproduces legacy ParticleAnimator localRotationAxis / worldRotationAxis behavior.
 * Documented legacy semantics (4.6 manual class-ParticleAnimator):
 *
 *   "Setting values in either the Local or World Rotation Axes will cause all spawned
 *    particles to rotate around the indicated axis (with the Transform's position as
 *    the center). The greater the value is entered on one of these axes, the faster
 *    the rotation will be."
 *   "Setting values in the Local Axes will cause the rotating particles to adjust
 *    their rotation as the Transform's rotation changes, to match its local axes."
 *   "Use the Rotation Axes to make whirlpool-like swirly motions."
 *
 * => ORBITAL MOTION: every particle's POSITION rotates around the emitter's center.
 *    This is NOT sprite orientation; Shuriken's RotationOverLifetime therefore maps
 *    nothing here (V2 conversion notes state this as well).
 *
 * DOCUMENTED UNCERTAINTIES (deliberately exposed, never hard-compensated):
 *
 *   1. Angular unit. Unity's docs give no degree/s vs rad/s rule. This script assumes
 *      axisMagnitude * orbitalSpeedMultiplier = DEGREES PER SECOND as the baseline;
 *      the multiplier is exposed for in-Inspector calibration against the legacy
 *      original (e.g. if legacy looks ~57x faster, set the multiplier to ~57.3).
 *   2. Whether legacy also rotates the particle VELOCITY vector (curved paths,
 *      whirlpool look) or only the position is undocumented. `rotateVelocity`
 *      defaults to true for the whirlpool reading, toggleable for an A/B.
 *   3. When both axes are nonzero their composition order is undocumented; the
 *      applied rotation is (worldAxis-step) * (localAxis-step), a single combined
 *      quaternion per frame (order-independent at the small angles in project data).
 *
 * UPDATE ORDER / SAFEST PRACTICAL APPROACH (Unity 2017.4):
 *
 *   The legacy engine rotated particles inside the native particle update pass.
 *   Shuriken has no per-frame simulation callback in 2017.4, so this script applies
 *   the orbital rotation in LateUpdate: after the engine's particle simulation and
 *   other scripts' Update/animation, just before rendering. Consequences:
 *     - positions/velocities it touches are the engine's output for this frame, and
 *       the engine integrates the written-back velocity NEXT frame: the orbit and
 *       the VelocityOverLifetime drift compose across frames with a sub-frame phase
 *       difference that is imperceptible at the speeds present in this project
 *       data, and preserves round-trip stability (position rotation never changes
 *       magnitudes, so velocities/damping stay consistent).
 *     - LateUpdate does not run in Edit mode, so the component is inert until Play.
 *
 * SIMULATION SPACES AND TRANSFORMS:
 *
 *   - Local simulation space: particle position/velocity read back in the emitter's
 *     LOCAL coordinates; the pivot is the local origin (the emitter center) and the
 *     localRotationAxis is used as authored (this inherits emitter rotation the way
 *     the manual describes). worldRotationAxis is converted to a local direction via
 *     the holder's worldToLocalMatrix (then normalized, speed taken from the
 *     original vector magnitude) - safe under nonuniform scale/scaled hierarchies.
 *   - World simulation space: particle coordinates are world; the pivot is the
 *     emitter's CURRENT world transform position each frame (moving emitters work);
 *     worldRotationAxis is used directly ("consistent regardless of the
 *     Transform's rotation"), localRotationAxis is converted local -> world via
 *     TransformDirection (rotation only, per the manual's local-axes-follow rule).
 *
 * PERFORMANCE:
 *
 *   One GetParticles/SetParticles round trip per frame with a cached buffer
 *   (allocated at maxParticles, grown only if that grows). Project data peaks at
 *   ~24 simultaneous particles; even the 32768 global cap is an O(n) quaternion
 *   loop with no steady-state allocation. 2017.4 has no particle jobs; none needed
 *   at these counts.
 *
 * NOT SUPPORTED / BEHAVIOR NOTES:
 *   - Particles with trails/subemitters attached below may render their trails from
 *     the (rotated) positions; verify visually if combined (none in current data).
 *   - remainingLifetime, size, color, rotation and all other Particle struct fields
 *     are passed through untouched; only the selected fields are rewritten.
 */

/// <summary>
/// Converts legacy ParticleAnimator rotation-axis orbital motion into Shuriken
/// particle positions. Attached ONLY to [PSV2] conversion holders by
/// "Tools/Particle System/Experiments/Attach Orbit Script (selection)".
/// </summary>
[AddComponentMenu("Legacy Particles V2/Legacy Particle Orbit")]
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public class LegacyParticleOrbit : MonoBehaviour
{
	[Header("Legacy axes (copied from the preserved ParticleAnimator)")]
	[Tooltip("Legacy ParticleAnimator.localRotationAxis (emitter-local frame; follows emitter rotation per the legacy manual).")]
	public Vector3 localRotationAxis = Vector3.zero;

	[Tooltip("Legacy ParticleAnimator.worldRotationAxis (kept consistent regardless of emitter rotation per the legacy manual).")]
	public Vector3 worldRotationAxis = Vector3.zero;

	[Header("Calibration (all deltas editable in this Inspector, no recompile)")]
	[Tooltip("Angular speed = axis magnitude * this multiplier, interpreted as DEGREES/second (unit unverified - calibrate against the legacy original).")]
	public float orbitalSpeedMultiplier = 1f;

	[Tooltip("Also rotate each particle's velocity vector like its position (whirlpool reading of the legacy behavior; toggle for A/B).")]
	public bool rotateVelocity = true;

	[Header("Switch")]
	[Tooltip("Applies the orbital simulation. OFF by default; enable after the legacy effect is visible for side-by-side calibration.")]
	public bool enableOrbitalSimulation = false;

	ParticleSystem m_ParticleSystem;
	ParticleSystem.Particle[] m_Buffer;

	void Awake()
	{
		m_ParticleSystem = GetComponent<ParticleSystem>();
	}

	void LateUpdate()
	{
		if (!enableOrbitalSimulation || m_ParticleSystem == null)
			return;

		bool localSim = m_ParticleSystem.main.simulationSpace == ParticleSystemSimulationSpace.Local;
		int maxParticles = m_ParticleSystem.main.maxParticles;
		EnsureBuffer(maxParticles);

		int count = m_ParticleSystem.GetParticles(m_Buffer);
		if (count <= 0)
			return;

		float dt = Time.deltaTime;
		if (dt <= 0f)
			return; // paused / editor

		Transform emitterT = m_ParticleSystem.transform;

		// Pivot of the orbit (documented: the emitter Transform's position).
		// Local sim: emitter local origin; World sim: current world position (moving emitters supported).
		Vector3 center = localSim ? Vector3.zero : emitterT.position;

		// Axis directions expressed in the same coordinate frame as the particle positions.
		// |axis| -> degrees/second THROUGH orbitalSpeedMultiplier (calibration input).
		float degPerSecLocal = localRotationAxis.magnitude * orbitalSpeedMultiplier;
		float degPerSecWorld = worldRotationAxis.magnitude * orbitalSpeedMultiplier;
		bool hasLocal = degPerSecLocal > 0f && localRotationAxis.sqrMagnitude > Mathf.Epsilon;
		bool hasWorld = degPerSecWorld > 0f && worldRotationAxis.sqrMagnitude > Mathf.Epsilon;
		if (!hasLocal && !hasWorld)
			return;

		Quaternion qLocal = Quaternion.identity;
		Quaternion qWorld = Quaternion.identity;
		if (hasLocal)
		{
			Vector3 dir = localSim
				? localRotationAxis // local coords: authored axis exactly as legacy stores it
				: TransformAxisLocalToWorld(emitterT, localRotationAxis); // local axes FOLLOW the emitter rotation
			qLocal = Quaternion.AngleAxis(degPerSecLocal * dt, dir.normalized);
		}
		if (hasWorld)
		{
			Vector3 dir = localSim
				? TransformAxisWorldToLocal(emitterT, worldRotationAxis) // world axes stay consistent vs emitter rotation; scale-safe inverse mapping
				: worldRotationAxis; // world coords: world axis as-is
			qWorld = Quaternion.AngleAxis(degPerSecWorld * dt, dir.normalized);
		}

		// Both axes share the same pivot, so the two per-frame steps compose into one
		// quaternion application per particle (order documented as an uncertainty above).
		Quaternion q = qWorld * qLocal;

		bool rotateVel = rotateVelocity;
		for (int i = 0; i < count; i++)
		{
			ParticleSystem.Particle p = m_Buffer[i];
			Vector3 rel = p.position - center;
			p.position = center + q * rel;
			if (rotateVel)
				p.velocity = q * p.velocity;
			m_Buffer[i] = p;
		}

		m_ParticleSystem.SetParticles(m_Buffer, count);
	}

	/// <summary>Cached buffer; only grows if the system's cap grows at runtime (minimizes GC).</summary>
	void EnsureBuffer(int maxParticles)
	{
		if (maxParticles < 1)
			maxParticles = 1;
		if (m_Buffer == null || m_Buffer.Length < maxParticles)
			m_Buffer = new ParticleSystem.Particle[maxParticles];
	}

	/// <summary>
	/// Local-frame axis direction expressed in world coordinates, rotation only
	/// (the manual's "local axes rotate with the Transform"). Under nonuniform scale
	/// a true local-axis orbit is shear-ill-defined; direction-only conversion is the
	/// documented approximation, speed kept from the original magnitude.
	/// </summary>
	static Vector3 TransformAxisLocalToWorld(Transform t, Vector3 axis)
	{
		Vector3 dir = t.TransformDirection(axis); // rotation-only
		if (dir.sqrMagnitude > Mathf.Epsilon)
			return dir;
		return Vector3.up; // degenerate: emitter fully flattened the axis; keep a stable axis
	}

	/// <summary>
	/// World-frame axis direction expressed in local particle coordinates.
	/// Uses the full world-to-local matrix (Scale-safe; a direction-only
	/// InverseTransformDirection would rotate but NOT descale the axis under a
	/// 0.01-scaled hierarchy), then relies on Quaternion.AngleAxis normalization.
	/// </summary>
	static Vector3 TransformAxisWorldToLocal(Transform t, Vector3 axis)
	{
		Vector3 dir = t.worldToLocalMatrix.MultiplyVector(axis);
		if (dir.sqrMagnitude > Mathf.Epsilon)
			return dir;
		return Vector3.up;
	}
}
