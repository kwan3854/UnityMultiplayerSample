using System;
using FishNet.Object;
using Game.Scripts.Navmesh;
using UnityEngine;

namespace Game.Scripts
{
    public abstract class ProjectileBase : NetworkBehaviour
    {
        public event Action<Collider> OnHit;
        public abstract void Shoot(RangedAttacker attacker, Damageable target);

        protected void RaiseOnHitVfx(Collider collision)
        {
            OnHit?.Invoke(collision);
        }
    }
}