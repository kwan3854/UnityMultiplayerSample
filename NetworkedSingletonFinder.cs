using System;
using System.Collections.Generic;
using System.Linq;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Game.Scripts
{
    public abstract class NetworkedSingletonFinder<T, C> : MonoBehaviour
        where T : NetworkedSingletonFinder<T, C>
        where C : NetworkedSingleton<T, C>
    {
        /// [FOR CLIENT ONLY] Get owned manager instance.
        /// Returns null if manager not found.
        public static C Instance
        {
            get
            {
                Debug.Assert(InstanceFinder.NetworkManager.IsClientStarted);
                
                if (_isInitialized)
                {
                    return _instance;
                }
                
                _instance = InstancesInternal.FirstOrDefault(c => c.IsOwner);

                if (_instance is not null)
                {
                    _isInitialized = true;
                    return _instance;
                }
                else
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Get all client's managers.
        /// </summary>
        public static List<C> Instances => InstancesInternal;

        /// <summary>
        /// Subscribes to events on the singleton instance. This method facilitates easy event subscription directly through the singleton. For more advanced usage, consider using the EventSubscriptionManager class.
        /// </summary>
        /// <param name="onSubscribe">The action to be executed once the subscription is ready and the instance is fully initialized.</param>
        /// <param name="onUnsubscribe">Optional action to execute upon unsubscription.</param>
        /// <param name="isSubscriptionReady">A condition that checks if the target manager is ready. Default is NetworkConditions.IsClientManagerReady.</param>
        /// <param name="subscriptionSettings">Configuration settings for subscription and unsubscription.</param>
        /// <returns>A subscription manager instance that handles the event subscription lifecycle.</returns>
        public static ConnectionEventManager<T, C> SubscribeEvent(Action<C> onSubscribe, Action<C> onUnsubscribe = null, Func<bool> isSubscriptionReady = null, SubscriptionSettings subscriptionSettings = null)
        {
            Debug.Assert(onSubscribe is not null);
            Debug.Assert(subscriptionSettings is { AsServer: true, TargetConnection: not null } or { AsServer: false, TargetConnection: null} or null);

            isSubscriptionReady ??= () => NetworkConditions.IsManagerReady<T, C>();
            var subscriptionManager = new ConnectionEventManager<T, C>(isSubscriptionReady, onSubscribe, onUnsubscribe, subscriptionSettings).Subscribe();
            
            return subscriptionManager;
        }
        
        private static T _selfInstance;
        private static C _instance;
        private static bool _isInitialized = false;
        private static readonly List<C> InstancesInternal = new();
        
        /// <summary>
        /// [FOR SERVER ONLY] Get manager object that matches with given client connection.
        /// </summary>
        /// <param name="client">Target client NetworkConnection</param>
        /// <returns>Manager object matches with given client connection.</returns>
        public static C GetInstanceByConnection(NetworkConnection client)
        {
            Debug.Assert(InstanceFinder.NetworkManager.IsServerStarted);
            
            var manager = Instances.FirstOrDefault(f => f.OwnerMatches(client));
            Debug.Assert(manager is not null);
            
            return manager;
        }

        private void Awake()
        {
            if (transform.parent != null && transform.root != null)
            {
                if (_selfInstance == null)
                {
                    _selfInstance = this as T;
                    DontDestroyOnLoad(this.transform.root.gameObject);
                }
                else if (_selfInstance != this)
                {
                    Destroy(gameObject);
                }
            }
            else
            {
                if (_selfInstance == null)
                {
                    _selfInstance = this as T;
                    DontDestroyOnLoad(this.gameObject);
                }
                else if (_selfInstance != this)
                {
                    Destroy(gameObject);
                }
            }
        }

        private void OnDestroy()
        {
            _selfInstance = null;
        }
    }
}