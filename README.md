SegNet Framework:

Some notes:
- Syntax is based on Mirror/Photon

Specifics:
- Put ALL transports and network objects UNDER NetworkManager GameObject (will be persistent). Game initialize process involves starting new scene and THEN loading game (scene objects that should be networked will be loaded up accordingly).
- NetworkBehaviours can be attached to scene objects (use boolean to mark). Spawning and destroying (despawn) can only be done through NetworkManager for any networked prefabs.
- On quit game, all instances for networking are destroyed
- RPC hook callback is called on host as well
- Limit of 64 SyncVars per NetworkBehaviour (ulong bitmask limit)