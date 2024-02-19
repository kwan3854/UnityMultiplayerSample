using FishNet.Object;
using UnityEngine;

namespace Game.Scripts
{
    public class NetworkedSingleton<T, C> : NetworkBehaviour
        where T : NetworkedSingletonFinder<T, C>
        where C : NetworkedSingleton<T, C>
    {
        private void Awake()
        {
            var component = GetComponent<C>();
            Debug.Assert(component is not null);
        
            if (!NetworkedSingletonFinder<T, C>.Instances.Contains(component))
            {
                NetworkedSingletonFinder<T, C>.Instances.Add(component);
            }
        
            var netObj = GetComponent<NetworkObject>();
            Debug.Assert(netObj is not null);
        
            if (!netObj.IsGlobal)
            {
                netObj.SetIsGlobal(true);
            }
        }
    }
}
