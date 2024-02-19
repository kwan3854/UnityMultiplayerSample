using FishNet;
using FishNet.Object;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Scripts.Navmesh
{
    [RequireComponent(typeof(CapsuleCollider))]
    public class RangedAttacker : AttackerUnitBase
    {
        [FormerlySerializedAs("projectile")]
        [BoxGroup, PropertySpace(0, 15)]
        [Title("Projectile object")]
        [InfoBox("If it is using particle system, make projectile particle system as child object, and assign to this.")]
        [SerializeField, AssetsOnly] private ProjectileBase projectilePrefab;
    
        /// <summary>
        /// Determines whether the attacker uses projectile object pooling. If true, projectiles 
        /// will be pooled for efficiency. Otherwise, a projectile being on/off for instant 
        /// attack effects.
        /// </summary>
        [BoxGroup, PropertySpace(15, 0)]
        [Title("Attack Type")]
        [InfoBox("Should this attacker use projectile pooling?")]
        [SerializeField] private bool isShooter; 
    
        private CapsuleCollider _capsuleCollider;

    
        public override void OnStartServer()
        {
            base.OnStartServer();
        
            _capsuleCollider = GetComponent<CapsuleCollider>();
            Debug.Assert(_capsuleCollider is not null);
        }

        [Server]
        protected override void RealAttack()
        {
            Debug.Assert(IsServerInitialized);
        
            if (!isShooter)
            {
                return;
            }
        
            ShootBullet();
        }

        [Server]
        private void ShootBullet()
        {
            Debug.Assert(IsServerInitialized);
        
            var bulletPrefab = projectilePrefab;
        
            // Initialize position and rotation of projectile.
            var radius = _capsuleCollider.radius;
            var t = transform;
            var attackerPosition = t.position;
            var position = attackerPosition + t.forward * (radius);
            position.y += _capsuleCollider.height / 2;
        
            // TODO: How much prewarm needed?
            var networkBullet = bulletPrefab.GetComponent<NetworkObject>();
            Debug.Assert(networkBullet is not null);
            var bullet = InstanceFinder.NetworkManager.GetPooledInstantiated(networkBullet, position, t.rotation, true);
            InstanceFinder.ServerManager.Spawn(bullet.gameObject, Owner);

            // Shoot
            var proj = bullet.GetComponent<ProjectileBase>();
            Debug.Assert(proj is not null);
            proj.Shoot(this, Target);
        }
    }
}
