using System.Collections.Generic;
using UnityEngine;

[AddComponentMenu("Game/DivineWindDamageAura")]
public class DivineWindHandler : AbilityHandlerComponent
{
	private const float kDamageFrequency = 0.25f;

	private float mDamagePerHit;

	private float mTimeUntilNextDamage;

	private float mRemainingDuration;

	private bool mStoppedEmitting;

	private ParticleSystem[] mEmitters;

	private void Start()
	{
		mEmitters = GetComponentsInChildren<ParticleSystem>();
		mRemainingDuration = abilitySchema.duration;
		mDamagePerHit = abilitySchema.damage / (mRemainingDuration / 0.25f);
		mTimeUntilNextDamage = 0f;
		mStoppedEmitting = false;
	}

	private void Update()
	{
		if (mStoppedEmitting)
		{
			bool flag = false;
			ParticleSystem[] array = mEmitters;
			foreach (ParticleSystem particleEmitter in array)
			{
				particleEmitter.Stop();
				if (particleEmitter.particleCount > 0)
				{
					flag = true;
				}
			}
			if (!flag)
			{
				GameObjectPool.DefaultObjectPool.Release(base.gameObject);
			}
			return;
		}
		mRemainingDuration -= Time.deltaTime;
		mTimeUntilNextDamage -= Time.deltaTime;
		while (mTimeUntilNextDamage <= 0f)
		{
			mTimeUntilNextDamage += 0.25f;
			List<Character> playerCharacters = WeakGlobalInstance<CharactersManager>.Instance.GetPlayerCharacters(1 - base.handlerObject.activatingPlayer);
			foreach (Character item in playerCharacters)
			{
				if (item != null && !(item is Gate))
				{
					mExecutor.PerformKnockback(item, 100, false, Vector3.zero);
					item.RecievedAttack(EAttackType.Wind, mDamagePerHit, mExecutor);
				}
			}
		}
		if (mRemainingDuration <= 0f)
		{
			mStoppedEmitting = true;
		}
	}
}
