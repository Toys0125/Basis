using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct BasisP2PSignalMessage
    {
        public const int MaxTokenLength = 64;
        public const int PublicKeySize = 32;

        public ushort otherPlayerId;
        public string sessionToken;
        /// <summary>
        /// X25519 ephemeral public key of the sender, relayed by the server so the two
        /// peers can derive a per-pair key and always encrypt the direct (P2P) link.
        /// </summary>
        public byte[] ephemeralPublicKey;
        // Optional trailing extension. Absence means the legacy single-address protocol.
        public byte discoveryVersion;
        public string discoveryCredential;
        public string discoveryIPv4Host;

        public void Deserialize(NetDataReader reader)
        {
            otherPlayerId = reader.GetUShort();
            sessionToken = reader.GetString(MaxTokenLength);
            byte hasKey = reader.GetByte();
            if (hasKey == 1 && reader.AvailableBytes >= PublicKeySize)
            {
                ephemeralPublicKey = new byte[PublicKeySize];
                reader.GetBytes(ephemeralPublicKey, PublicKeySize);
            }
            else
            {
                ephemeralPublicKey = null;
            }
            discoveryVersion = 0;
            discoveryCredential = null;
            discoveryIPv4Host = null;
            if (reader.AvailableBytes > 0)
            {
                discoveryVersion = reader.GetByte();
                if (discoveryVersion == 2)
                {
                    discoveryCredential = reader.GetString(MaxTokenLength);
                    discoveryIPv4Host = reader.GetString(255);
                }
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(otherPlayerId);
            writer.Put(sessionToken ?? string.Empty, MaxTokenLength);
            if (ephemeralPublicKey != null && ephemeralPublicKey.Length == PublicKeySize)
            {
                writer.Put((byte)1);
                writer.Put(ephemeralPublicKey);
            }
            else
            {
                writer.Put((byte)0);
            }
            if (discoveryVersion == 2)
            {
                writer.Put(discoveryVersion);
                writer.Put(discoveryCredential ?? string.Empty, MaxTokenLength);
                writer.Put(discoveryIPv4Host ?? string.Empty, 255);
            }
        }
    }
}
