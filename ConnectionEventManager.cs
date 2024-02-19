using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using FishNet;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Scripts
{
    public class SubscriptionSettings
    {
        public bool AsServer { get; set; } = false;
        public NetworkConnection TargetConnection { get; set; } = null;
        public LocalConnectionState SubscribeTiming { get; set; }
        public LocalConnectionState UnsubscribeTiming { get; set; }

        public SubscriptionSettings(bool asServer, NetworkConnection targetConnection, LocalConnectionState subscribeTiming, LocalConnectionState unsubscribeTiming)
        {
            AsServer = asServer;
            TargetConnection = targetConnection;
            SubscribeTiming = subscribeTiming;
            UnsubscribeTiming = unsubscribeTiming;
        }

        public static readonly SubscriptionSettings StaticClient = new(false, null, LocalConnectionState.Started, LocalConnectionState.Stopping);
    }
    
    /// <summary>
    /// Manages subscriptions to the ClientManager.OnClientConnectionState or ServerManager.OnServerConnectionState event for networked singletons.
    /// This class provides a structured way to handle event subscriptions and unsubscriptions related to network state changes.
    /// Utilize this class to manage event subscriptions within a NetworkedSingleton context.
    /// </summary>
    /// <typeparam name="T">The type of the NetworkedSingletonFinder managing the singleton instance.</typeparam>
    /// <typeparam name="C">The type of the NetworkedSingleton for which events are being subscribed or unsubscribed.</typeparam>
    public class ConnectionEventManager<T, C>
        where T : NetworkedSingletonFinder<T, C>
        where C : NetworkedSingleton<T, C>
    {
        private readonly Func<bool> _isSubscriptionReady;
        private readonly Action<C> _onSubscribe;
        private readonly Action<C> _onUnsubscribe;
        private readonly SubscriptionSettings _settings;

        /// <summary>
        /// Initializes a new instance of the EventSubscriptionManager class, designed to manage event subscriptions
        /// based on the network connection state. This manager allows for precise control over subscription and unsubscription,
        /// depending on network conditions and provided settings.
        /// </summary>
        /// <param name="isSubscriptionReady">A predicate function that evaluates if the subscription conditions are met.</param>
        /// <param name="onSubscribe">Action to execute upon subscription.</param>
        /// <param name="onUnsubscribe">Optional action to execute upon unsubscription.</param>
        /// <param name="settings">Configuration settings for subscription and unsubscription.</param>
        public ConnectionEventManager(Func<bool> isSubscriptionReady, Action<C> onSubscribe, Action<C> onUnsubscribe = null, SubscriptionSettings settings = null)
        {
            _isSubscriptionReady = isSubscriptionReady;
            _onSubscribe = onSubscribe;
            _onUnsubscribe = onUnsubscribe;
            _settings = settings ?? SubscriptionSettings.StaticClient;
            
            Debug.Assert(!_settings.AsServer || (_settings.AsServer && _settings.TargetConnection != null), "Server mode requires a valid target connection.");
        }
        
        /// <summary>
        /// Subscribes to the OnClientConnectionState or OnServerConnectionState event.
        /// </summary>
        /// <returns>The current instance of EventSubscriptionManager.</returns>
        public ConnectionEventManager<T, C> Subscribe()
        {
            if (_settings.AsServer)
            {
                InstanceFinder.ServerManager.OnServerConnectionState += ManageServerSubscription;
            }
            else
            {
                InstanceFinder.ClientManager.OnClientConnectionState += ManageClientSubscription;
            }
            return this;
        }
        
        /// <summary>
        /// Unsubscribes from the OnClientConnectionState or OnServerConnectionState event.
        /// Use this method to stop listening to connection state changes.
        /// To manage event unsubscriptions within the NetworkedSingleton, specify unsubscription actions and timing when initializing the class.
        /// </summary>
        public void Unsubscribe()
        {
            if (_settings.AsServer)
            {
                InstanceFinder.ServerManager.OnServerConnectionState -= ManageServerSubscription;
            }
            else
            {
                InstanceFinder.ClientManager.OnClientConnectionState -= ManageClientSubscription;
            }
            
        }
        
        private void ManageClientSubscription(ClientConnectionStateArgs con)
        {
            var action = con.ConnectionState == _settings.SubscribeTiming ? _onSubscribe : (con.ConnectionState == _settings.UnsubscribeTiming ? _onUnsubscribe : null);

            if (action != null)
            {
                ManageSubscription(con.ConnectionState, Predicate, action).Forget();
            }

            return;

            bool Predicate(C i) => i.IsOwner;
        }
        
        private void ManageServerSubscription(ServerConnectionStateArgs con)
        {
            var action = con.ConnectionState == _settings.SubscribeTiming ? _onSubscribe : (con.ConnectionState == _settings.UnsubscribeTiming ? _onUnsubscribe : null);

            if (action != null)
            {
                ManageSubscription(con.ConnectionState, Predicate, action).Forget();
            }

            return;

            bool Predicate(C i) => i.OwnerMatches(_settings.TargetConnection);
        }
        
        private async UniTask ManageSubscription(LocalConnectionState connectionState, Func<C, bool> instancePredicate, Action<C> action)
        {
            if (connectionState == _settings.SubscribeTiming || connectionState == _settings.UnsubscribeTiming)
            {
                await UniTask.WaitUntil(_isSubscriptionReady);
        
                var instances = Object.FindObjectsByType<C>(FindObjectsSortMode.None);
                Debug.Assert(
                    (connectionState == _settings.SubscribeTiming && instances.Count(instancePredicate) == 1) || 
                    (connectionState == _settings.UnsubscribeTiming && instances.Count(instancePredicate) <= 1), 
                    "Subscription condition failed: Subscribe timing should have exactly one instance, unsubscribe timing should have at most one instance."
                );
                
                var instance = instances.FirstOrDefault(instancePredicate);
        
                if (instance != null)
                {
                    action?.Invoke(instance);
                }
            }
        }
    }
}