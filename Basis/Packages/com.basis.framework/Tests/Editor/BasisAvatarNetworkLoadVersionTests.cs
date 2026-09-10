using System.IO;
using System.IO.Compression;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;

public class BasisAvatarNetworkLoadVersionTests
{
    [Test]
    public void VersionTag_RoundTripsWithAvatarChangePayload()
    {
        var message = new BasisAvatarNetworkLoad
        {
            URL = "https://example.com/avatar.bee",
            UnlockPassword = "password",
            VersionTag = "W/\"etag-v2\"",
        };

        BasisAvatarNetworkLoad decoded = BasisAvatarNetworkLoad.DecodeFromBytes(message.EncodeToBytes());

        Assert.AreEqual(message.URL, decoded.URL);
        Assert.AreEqual(message.UnlockPassword, decoded.UnlockPassword);
        Assert.AreEqual(message.VersionTag, decoded.VersionTag);
    }

    [Test]
    public void LegacyTwoFieldAvatarPayload_DecodesWithEmptyVersionTag()
    {
        byte[] compressed;
        using (var raw = new MemoryStream())
        {
            using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                WriteLegacyString(writer, "https://example.com/legacy-avatar.bee");
                WriteLegacyString(writer, "legacy-password");
            }

            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] bytes = raw.ToArray();
                deflate.Write(bytes, 0, bytes.Length);
            }
            compressed = output.ToArray();
        }

        BasisAvatarNetworkLoad decoded = BasisAvatarNetworkLoad.DecodeFromBytes(compressed);

        Assert.AreEqual("https://example.com/legacy-avatar.bee", decoded.URL);
        Assert.AreEqual("legacy-password", decoded.UnlockPassword);
        Assert.AreEqual(string.Empty, decoded.VersionTag);
    }

    private static void WriteLegacyString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }
}
