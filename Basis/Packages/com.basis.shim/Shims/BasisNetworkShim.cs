using static BasisNetworkCommon;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using Cilbox;

namespace Basis.Shims
{
	public class BasisNetworkShim : BasisNetworkBehaviour, IBasisNetworkShimCompatible, CilboxShimI
	{
		public override NetworkReadyEvent NetworkReady
		{
			get => base.NetworkReady;
			set => base.NetworkReady = value;
		}
		public override OwnershipTransferEvent OwnershipTransfer
		{
			get => base.OwnershipTransfer;
			set => base.OwnershipTransfer = value;
		}
		public override ServerOwnershipDestroyedEvent ServerOwnershipDestroyed
		{
			get => base.ServerOwnershipDestroyed;
			set => base.ServerOwnershipDestroyed = value;
		}
		public override NetworkMessageEvent NetworkMessageReceived
		{
			get => base.NetworkMessageReceived;
			set => base.NetworkMessageReceived = value;
		}
		public override PlayerLeftEvent PlayerLeft
		{
			get => base.PlayerLeft;
			set => base.PlayerLeft = value;
		}
		public override PlayerJoinedEvent PlayerJoined
		{
			get => base.PlayerJoined;
			set => base.PlayerJoined = value;
		}

		bool IBasisNetworkShimCompatible.IsOwnedLocallyOnServer => IsOwnedLocallyOnServer;
		bool IBasisNetworkShimCompatible.IsOwnedLocallyOnClient => IsOwnedLocallyOnClient;
		ushort IBasisNetworkShimCompatible.CurrentOwnerId => CurrentOwnerId;
		BasisNetworkPlayer IBasisNetworkShimCompatible.currentOwnedPlayer => currentOwnedPlayer;

        public override void OnNetworkReady()
        {
			NetworkReady?.Invoke();
        }
        public override void OnServerOwnershipDestroyed()
        {
			ServerOwnershipDestroyed?.Invoke();
        }
        public override void OnOwnershipTransfer(BasisNetworkPlayer NetNewOwner)
        {
			OwnershipTransfer?.Invoke(NetNewOwner);
        }
        public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
			NetworkMessageReceived?.Invoke( PlayerID, buffer, DeliveryMethod );
        }
        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
			PlayerLeft?.Invoke( player );
        }
        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
			PlayerJoined?.Invoke( player );
        }

        public override void RequestOwnershipIfNone()
        {
	        _ = RequestWhoIsOwnershipAsync();
        }
	}
}
