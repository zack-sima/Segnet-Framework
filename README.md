# SegNet Framework:

## Installation

1. Install Steamworks:
Window > Package Manager > + > Add package from git URL
https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2025.162.1

2. Install SegNet
Window > Package Manager > + > Add package from git URL
https://github.com/zack-sima/Segnet-Framework.git?path=/Assets/Scripts/SegNet

## Setup

1. Create an empty GameObject named NetworkManager.
2. Add the NetworkManager component from SegNet.
3. Create a prefab registry with Create > SegNet > Prefab Registry.
4. Add your networked prefabs to the registry.
5. Assign the registry to the NetworkManager.
6. Set Game Scene, Menu Scene, Use Steam Transport, Steam Room Number, Local Address, and Local Port as needed.

In scripts, write using SegNet; to reference classes.

To have a real running game with NetworkBehaviours, you will want to inherit from the BaseNetworkManager with your custom class (suggested: NetworkManager) to set up player controls. See full repo sample scripts.

Start sessions with:

NetworkManager.Instance.StartHost();
NetworkManager.Instance.StartClient();
NetworkManager.Instance.StartServer();
NetworkManager.Instance.StopGame();

## More

Some notes:
- Syntax is based on Mirror/Photon

Specifics:
- Put ALL transports and network objects UNDER NetworkManager GameObject (will be persistent). Game initialize process involves starting new scene and THEN loading game (scene objects that should be networked will be loaded up accordingly).
- NetworkBehaviours can be attached to scene objects (use boolean to mark). Spawning and destroying (despawn) can be done through either NetworkManager or through static functions on NetworkBehaviour as InstantiateNetworked or DestroyNetworked. Only destroy root objects!
- On quit game, all instances for networking are destroyed
- RPC hook callback is called on host as well
- Limit of 64 SyncVars per NetworkBehaviour (ulong bitmask limit)
- Use NetworkManager's kbIn and kbOut for debugging bandwidth used

Synced Collections:

SyncArray<T>:

array[index]
array[index] = value
array.Set(index, value)
array.Clear()
array.Length
foreach (var item in array)
SyncList<T>:

list[index]
list[index] = value
list.Set(index, value)
list.Add(value)
list.Insert(index, value)
list.Remove(value)
list.RemoveAt(index)
list.Clear()
list.Contains(value)
list.IndexOf(value)
list.Count
foreach (var item in list)
SyncDict<TKey, TValue>:

dict[key]
dict[key] = value
dict.Get(key)
dict.Set(key, value)
dict.Add(key, value)
dict.Remove(key)
dict.Clear()
dict.HasKey(key)
dict.ContainsKey(key)
dict.TryGetValue(key, out value)
dict.Count
dict.Keys
dict.Values
foreach (var kvp in dict)
SyncHashSet<T>:

set.Add(value)
set.Remove(value)
set.Clear()
set.Contains(value)
set.Has(value)
set.Count
foreach (var item in set)