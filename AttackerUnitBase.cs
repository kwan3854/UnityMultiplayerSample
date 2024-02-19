using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet;
using FishNet.Component.Animating;
using FishNet.Object;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.AI;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using Sirenix.OdinInspector.Editor;
#endif

namespace Game.Scripts.Navmesh
{
    [RequireComponent(typeof(NavMeshAgent), typeof(SelectableUnit), typeof(Animator))]
    [InfoBox("Attack animation must have \'Attack\' State. And triggers named \'startAttack\', \'stopAttack\' parameters and float \'attackSpeed\' parameter.", InfoMessageType.Warning)]
    [InfoBox("Core concept: \nTotal attack duration == preDelay + postDelay\nDuring preDelay: Unit can cancel attack and move.\nDuring postDelay: Unit can't cancel attack and can't move.")]
    public abstract class AttackerUnitBase: NetworkBehaviour
    {
        public event Action OnStartAttackState;
        public event Action OnStopAttackState;
        public event Action OnStartAttackAnimation;
        public event Action OnRealAttackServerOnly;
        public event Action OnStopAttackAnimation;
    
        protected Damageable Target;
    
        [BoxGroup("AttackDelaySettings", ShowLabel = false), PropertySpace(15, 0)]
        [Title("Attack Delay Settings")]
        [SerializeField] private float preDelay;
        [BoxGroup("AttackDelaySettings")]
        [SerializeField] private float postDelay;
        [BoxGroup("AttackDelaySettings"), PropertySpace(15, 15)]
        [InfoBox("Attack animation length should be set by button below.(\'Initialize Attack Animation Length\' button)")]
        [SerializeField, HideInInspector] private float attackAnimationLength = 0;

        private MonsterStatComponent _stat;
        private SelectableUnit _selectableUnit;
        private Animator _animator;
        private NetworkAnimator _networkAnimator;
        private NavMeshAgent _agent;
    
        private CancellationTokenSource _attackRoutineCts;
        private CancellationTokenSource _chaseAttackRoutineCts;
        private CancellationTokenSource _lateMoveCts;
        private CancellationTokenSource _lateAttackCts;

        private Vector3 _lateMovePositionBuffer;
    
        private float _attackRange;
        private float _sightRange;
        private float _preAttackEndTime;
        private float _postAttackEndTime;
        private bool _isAttackCommanded;
        private bool _isTargetExist;
        private bool _isAttacking;
    
        private static readonly int StartAttack = Animator.StringToHash("startAttack");
        private static readonly int StopAttack = Animator.StringToHash("stopAttack");
        private static readonly int AttackState = Animator.StringToHash("Attack");
        private static readonly int AttackSpeed = Animator.StringToHash("attackSpeed");
   

#if UNITY_EDITOR
        private const string AttackStateName = "Attack";
#endif
    
        private bool IsPreAttackDelaying
        {
            [Server] get => Time.time < _preAttackEndTime;
        }
    
        private bool IsPostAttackDelaying
        {
            [Server] get => Time.time >= _preAttackEndTime && Time.time < _postAttackEndTime;
        }
    
        private bool IsTargetInAttackRange
        {
            [Server]
            get
            {
                Debug.Assert(Target is not null);

                var targetCollider = Target.GetComponent<Collider>();
                Debug.Assert(targetCollider is not null);

                var position = transform.position;
                var closestPoint = targetCollider.ClosestPoint(position);
                var distance = Vector3.Distance(position, closestPoint);

                var result = distance <= _attackRange;
                return result;
            }
        }

        private bool IsTargetVisible
        {
            [Server]
            get
            {
                // TODO: Visibility validation logic not completely implemented.
                Debug.Assert(Target is not null);

                var targetCollider = Target.GetComponent<Collider>();
                Debug.Assert(targetCollider is not null);

                var position = transform.position;
                var closestPoint = targetCollider.ClosestPoint(position);
                var distance = Vector3.Distance(position, closestPoint);

                var result = distance <= _sightRange;
                return result;
            }
        }

        [Client]
        public void StartAttackState(Damageable target)
        {
            Debug.Assert(target is not null
                         && target.IsOwner is false
                         && Owner == InstanceFinder.ClientManager.Connection
                         && IsClientInitialized);
        
            if (IsClientInitialized)
            {
                StartAttackStateServerRpc(target);
            }
        }

        [Server]
        public void StopAttackState()
        {
            Debug.Assert(IsServerInitialized);
        
            StopAttackStateLogic();
        }
    
        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
        
            _animator = GetComponent<Animator>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
        

            _networkAnimator = GetComponent<NetworkAnimator>();
            _agent = GetComponent<NavMeshAgent>();
            _stat = GetComponent<MonsterStatComponent>();
            Debug.Assert(_networkAnimator is not null
                         && _agent is not null
                         && _stat is not null);
        
            _attackRange = _stat.MonsterData.startAtkRange;
            _sightRange = _stat.MonsterData.startSightRange;

            InitializeAttackAnimationSpeed();
        }
    
        protected abstract void RealAttack();

        private void Awake()
        {
            _selectableUnit = GetComponent<SelectableUnit>();
            
            OnStartAttackState += StartChaseAttackRoutine;
            OnStopAttackState += StopAttackRoutine;
            OnStopAttackState += StopChaseAttackRoutine;
            OnStopAttackState += StopAnimateAttackAnimation;
            OnRealAttackServerOnly += RealAttack;
            OnStartAttackAnimation += AnimateStartAttackAnimation;
            OnStartAttackAnimation += LookAtTarget;
            OnStopAttackAnimation += StopAnimateAttackAnimation;
            _selectableUnit.OnMove += OverrideMove;
        }

        private void OnDestroy()
        {
            Debug.Assert(_selectableUnit is not null);
            
            OnStartAttackState -= StartChaseAttackRoutine;
            OnStopAttackState -= StopAttackRoutine;
            OnStopAttackState -= StopChaseAttackRoutine;
            OnStopAttackState -= StopAnimateAttackAnimation;
            OnRealAttackServerOnly -= RealAttack;
            OnStartAttackAnimation -= AnimateStartAttackAnimation;
            OnStartAttackAnimation -= LookAtTarget;
            OnStopAttackAnimation -= StopAnimateAttackAnimation;
            _selectableUnit.OnMove -= OverrideMove;
        }

        [ServerRpc]
        private void StartAttackStateServerRpc(Damageable target)
        {
            StartAttackStateLogic(target);
        }
    
        /// <summary>
        /// NetworkAnimator doesn't sync animation frame by frame.
        /// This method is for sync start timing of attack animation.
        /// </summary>
        [ObserversRpc]
        private void StartAttackAnimationObserversRpc()
        {
            _animator.Play(AttackState, 0, 0.0f);
        }

        private void OnDisable()
        {
            _lateAttackCts?.Cancel();
            _lateMoveCts?.Cancel();
        }

        [Server]
        private void StartAttackStateLogic(Damageable target)
        {
            Debug.Assert(target is not null);
            Debug.Assert(IsServerInitialized);
            Debug.Assert(Owner != target.Owner);
        
            Target = target;
            _isTargetExist = true;

            // if during postDelay, don't start new attack.
            // buffer attack command and do attack when delay finished.
            if (IsPostAttackDelaying)
            {
                _lateAttackCts?.Cancel();
                _lateAttackCts = new CancellationTokenSource();
            
                StartLateAttack(_lateAttackCts.Token).Forget();
                return;
            }
        
            OnStartAttackState?.Invoke();
        }
    
        [Server]
        private void StopAttackStateLogic()
        {
            Debug.Assert(IsServerInitialized);
        
            Target = null;
            _isTargetExist = false;
        
            OnStopAttackState?.Invoke();
        }
    
        [Server]
        private void StartChaseAttackRoutine()
        {
            Debug.Assert(!IsPostAttackDelaying);
        
            // if it is attack commanded, stop attack state and start new attack state.
            if (_isAttackCommanded)
            {
                OnStopAttackState?.Invoke();
            }

            // start new chase attack routine
            _isAttackCommanded = true;

            _chaseAttackRoutineCts?.Cancel();
            _chaseAttackRoutineCts = new CancellationTokenSource();
            StartChaseAttackRoutine(_chaseAttackRoutineCts.Token).Forget();
        }
    
        [Server]
        private void StopChaseAttackRoutine()
        {
            _isAttackCommanded = false;
            _chaseAttackRoutineCts?.Cancel();
        }
    
        [Server]
        private void StopAttackRoutine()
        {
            _isAttacking = false;
            _attackRoutineCts?.Cancel();
        }
    
        [Server]
        private void InitializeAttackAnimationSpeed()
        {
            var totalDuration = preDelay + postDelay;
            Debug.Assert(preDelay > 0
                         && postDelay > 0);

            var newSpeed = attackAnimationLength / totalDuration;
            Debug.Assert(attackAnimationLength != 0);
        
            _animator.SetFloat(AttackSpeed, newSpeed);
        }
    

        [Server]
        private void LookAtTarget()
        {
            if (_isTargetExist is false
                || Target == null)
            {
                return;
            }
        
            var targetPosition = Target.transform.position;
            var currentPosition = transform.position;
        
            // look at target
            // make sure rotate just y-axis(euler)
            var direction = new Vector3(targetPosition.x, currentPosition.y, targetPosition.z) - currentPosition;
            var lookRotation = Quaternion.LookRotation(direction.normalized);
        
            lookRotation = Quaternion.Euler(0, lookRotation.eulerAngles.y, 0);
            transform.rotation = lookRotation;
        }

        [Server]
        private void AnimateStartAttackAnimation()
        {
            // Instant initiating animation
            _networkAnimator.ResetTrigger(StopAttack); // in case of trigger sync hickups
            _networkAnimator.ResetTrigger(StartAttack);
        
            _networkAnimator.SetTrigger(StartAttack);
        
            // to exact matching looping animation
            _animator.Play(AttackState, 0, 0.0f);
            StartAttackAnimationObserversRpc();
        }

        [Server]
        private void StopAnimateAttackAnimation()
        {
            _networkAnimator.ResetTrigger(StartAttack); // in case of trigger sync hickups
            _networkAnimator.ResetTrigger(StopAttack);
        
            _networkAnimator.SetTrigger(StopAttack);
        }

        /// <summary>
        /// override SelectableUnit class MoveTo() function.
        /// </summary>
        /// <param name="position">position to move.</param>
        [Server]
        private void OverrideMove(Vector3 position)
        {
            Debug.Assert(IsServerInitialized);
        
            switch (_isAttacking)
            {
                // if during preDelay, cancel attack and move.
                case true when IsPreAttackDelaying:
                {
                    StopAttackState();
                    _agent.SetDestination(position);
                    break;
                }
                // if during postDelay, pend move.
                case true when IsPostAttackDelaying:
                {
                    _agent.SetDestination(transform.position); // to override default move command.
                    _lateMovePositionBuffer = position;
                
                    _lateMoveCts?.Cancel();
                    _lateMoveCts = new CancellationTokenSource();
                
                    StartLateMove(_lateMoveCts.Token).Forget();
                    break;
                }
                // if all attack routine finished, don't override.
                default:
                {
                    // DO NOTHING
                    break;
                }
            }
        }

        [Server]
        private async UniTask StartChaseAttackRoutine(CancellationToken cancellationToken)
        {
            while (_isAttackCommanded)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);

                // TODO: Instead of null checking, check if it is dead by boolean.
                // TODO: After monster died, there should be dying motion, not despawn instantly.
                if (!_isTargetExist || Target == null)
                {
                    _isTargetExist = false;
                    break;
                }

                if (!IsTargetVisible)
                {
                    // if target is out of sight, stop chasing, stop attack.
                    StopAttackState();
                    _selectableUnit.StopMove();
                }
                else if (!IsTargetInAttackRange)
                {
                    // Chase
                    // if target is in sight but out of attack range, chase.
                    if (_isAttacking)
                    {
                        // Don't start move until it finish its current attack.
                        continue;
                    }

                    _selectableUnit.MoveTo(Target.transform.position);
                    OnStopAttackAnimation?.Invoke();
                }
                else
                {
                    // Attack
                    // stop moving and start attack
                    _selectableUnit.StopMove();
                    if (_isAttacking)
                    {
                        continue;
                    }
                
                    _attackRoutineCts?.Cancel();
                    _attackRoutineCts = new CancellationTokenSource();

                    StartAttackRoutine(_attackRoutineCts.Token).Forget();
                }
            }
        }

        [Server]
        private async UniTask StartAttackRoutine(CancellationToken cancellationToken)
        {
            // 1. Start attack animation
            _isAttacking = true;
            OnStartAttackAnimation?.Invoke();

            // 2. Calc PreAttackEndTime and wait
            _preAttackEndTime = Time.time + preDelay;
            await UniTask.WaitForSeconds(preDelay, cancellationToken:cancellationToken);
        
            // 3. Real attack. ex) shoot bullet, swing sword...
            OnRealAttackServerOnly?.Invoke();

            // 4. Calc PostAttackEndTime and wait
            _postAttackEndTime = Time.time + postDelay;
            await UniTask.WaitForSeconds(postDelay, cancellationToken:cancellationToken);
        
            // 5. End attack routine.
            _isAttacking = false;
            OnStopAttackAnimation?.Invoke();
        }

        [Server]
        private async UniTask StartLateAttack(CancellationToken cancellationToken)
        {
            Debug.Assert(Time.time > _preAttackEndTime);

            // while postDelay don't start new attack
            await UniTask.WaitWhile(() => IsPostAttackDelaying, cancellationToken: cancellationToken);

            OnStartAttackState?.Invoke();
        }

        [Server]
        private async UniTask StartLateMove(CancellationToken cancellationToken)
        {
            Debug.Assert(Time.time > _preAttackEndTime);
        
            await UniTask.WaitWhile(() => IsPostAttackDelaying, cancellationToken: cancellationToken);

            OnStopAttackState?.Invoke();
            _agent.SetDestination(_lateMovePositionBuffer);
        }

#if UNITY_EDITOR
        [PropertySpace(30, 30)]
        [Button("Initialize Attack Animation Length", ButtonSizes.Large), GUIColor(0, 1, 0)]
        [InfoBox("Button for setting Attack Animation Length")]
        public void GetAnimationLength()
        {
            var animator = GetComponent<Animator>();
        
            if (animator == null)
            {
                Debug.LogError("Animator component not found.");
                return;
            }
        
            var animatorController = animator.runtimeAnimatorController as AnimatorController;
        
            if (animatorController is null)
            {
                Debug.LogError("AnimatorController not found.");
                return;
            }
        
            foreach (var layer in animatorController.layers)
            {
                GetLengthFromStateMachine(layer.stateMachine);
            }
        }

        private void GetLengthFromStateMachine(AnimatorStateMachine stateMachine)
        {
            foreach (var state in stateMachine.states)
            {
                var motion = state.state.motion;
                if (motion is AnimationClip clip)
                {
                    if (state.state.name == AttackStateName)
                    {
                        attackAnimationLength = clip.length;
                        return;
                    }
                }
                // Recursively check sub-state machines
                foreach (var subStateMachine in stateMachine.stateMachines)
                {
                    GetLengthFromStateMachine(subStateMachine.stateMachine);
                }
            }
        }
#endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(AttackerUnitBase), true)]
    public class AttackerUnitEditor : OdinEditor
    {
        protected override void OnEnable()
        {
            base.OnEnable();
            var attacker = (AttackerUnitBase)target;
        
            attacker.GetAnimationLength();
        }
    }
#endif
}