using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Object;
using Game.Scripts.Navmesh;
using UnityEngine;

namespace Game.Scripts
{
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public class Projectile : ProjectileBase
    {
        [SerializeField] private float speed = 10;
        [SerializeField] private float destroyDelay = 0.5f;
    
        private RangedAttacker _rangedAttacker;
        private NetworkObject _networkObject;
        private CancellationTokenSource _flyToTargetCts;
        private CancellationTokenSource _delayedDespawnCts;
    
        private bool _isCritical;
        private bool _isHitTarget;
        private float _damage;
        private float _criticalChance;
        private float _criticalDamage;
    
        [Server]
        public override async void Shoot(RangedAttacker attacker, Damageable target)
        {
            await UniTask.WaitUntil(() => IsSpawned, cancellationToken: new CancellationToken());

            Debug.Assert(attacker is not null);
            Debug.Assert(!target.OwnerId.Equals(OwnerId), "Bullet shot to ally.");
        
            _rangedAttacker = attacker;
            Debug.Assert(_rangedAttacker.OwnerId.Equals(OwnerId), "Owner of Bullet and Attacker should be same.");
        
            InitializeStats();

            transform.position = _rangedAttacker.transform.position;
        
            _flyToTargetCts?.Cancel();
            _flyToTargetCts = new CancellationTokenSource();

            var targetCollider = target.GetComponent<Collider>();
            Debug.Assert(targetCollider is not null);
        
            FlyToTarget(targetCollider, _flyToTargetCts.Token).Forget();
        }

        [Server]
        private void InitializeStats()
        {
            Debug.Assert(_rangedAttacker is not null);
        
            // get stats
            var stat = _rangedAttacker.GetComponent<MonsterStatComponent>();
            Debug.Assert(stat is not null);
        
            _damage = Mathf.Clamp(stat.CurrentAD, 0, float.MaxValue);
            _criticalChance = Mathf.Clamp(stat.CurrentCritPercent / 100, 0, 1f);
            _criticalDamage = Mathf.Clamp(stat.CurrentCritDmgPercent / 100, 1f, 5f);
        
            // TODO: damage weight by item, skills, level.
        }
    

        private void Awake()
        {
            GetComponent<Rigidbody>().isKinematic = false;
            GetComponent<Collider>().isTrigger = false;

            _networkObject = GetComponent<NetworkObject>();
            Debug.Assert(_networkObject is not null);
        }

        private void OnEnable()
        {
            _isHitTarget = false;
        }

        private void OnDisable()
        {
            _flyToTargetCts?.Cancel();
            _delayedDespawnCts?.Cancel();
        
            _isHitTarget = false;
        }
    

        private async void OnTriggerEnter(Collider other)
        {
            await UniTask.WaitUntil(() => IsSpawned, cancellationToken: new CancellationToken());

            if (!IsServerInitialized)
            {
                return;
            }
            
            // ======Server Only======
            Debug.Assert(IsServerInitialized);
            
            // Check if it is target
            if (!IsValidHit(other.gameObject, out var target))
            {
                return;
            }
        
            var damageable = other.GetComponent<Damageable>();
            Debug.Assert(damageable is not null);
            RaiseOnHitVfxObserversRpc(damageable);
        
            _flyToTargetCts?.Cancel();
        
            Debug.Assert(target is not null);
            DealDamage(target);

            _delayedDespawnCts?.Cancel();
            _delayedDespawnCts = new CancellationTokenSource();
            StartDelayedDespawn(destroyDelay, _delayedDespawnCts.Token).Forget();
        }

        [ObserversRpc]
        private void RaiseOnHitVfxObserversRpc(Damageable target)
        {
            var targetCollider = target.GetComponent<Collider>();
            RaiseOnHitVfx(targetCollider);
        }

        [Server]
        private async UniTask FlyToTarget(Collider targetCollider, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // TODO: 사망 판정 boolean 값을 먼저 비교, disconnection 등의 문제를 위해 null 체크는 나중에.
                if (targetCollider == null)
                {
                    GetComponent<NetworkObject>().Despawn();
                    return;
                }

                var targetPosition = targetCollider.bounds.center;
                transform.position = Vector3.MoveTowards(transform.position, targetPosition, speed * Time.deltaTime);
            
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
            }
        }
    
        [Server]
        private bool IsValidHit(GameObject target, out Damageable unit)
        {
            Debug.Log(target.name);
            Debug.Assert(target != null);
            Debug.Assert(IsSpawned);
        
            var isValid = target.TryGetComponent(out unit)
                          //&& _target == unit // TODO: Hit to a target only? or an any enemy?
                          && unit.OwnerId != OwnerId
                          && !_isHitTarget;

            if (isValid)
            {
                _isHitTarget = true;
            }
        
            return isValid;
        }
    
        [Server]
        private void DealDamage(Damageable target)
        {
            Debug.Assert(target is not null);
        
            Debug.Assert(_damage >= 0
                         && _criticalChance >= 0
                         && _criticalDamage >= 1);
        
            target.Damage(_damage, _criticalChance, _criticalDamage);
        }

        [Server]
        private async UniTask StartDelayedDespawn(float delay, CancellationToken cancellationToken)
        {
            await UniTask.WaitForSeconds(delay, cancellationToken: cancellationToken);
        
            // return to pool
            _networkObject.Despawn(DespawnType.Pool);
        }
    }
}