# SegNet Framework:

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