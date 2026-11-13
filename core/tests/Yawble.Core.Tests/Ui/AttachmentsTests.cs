using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>Images attached to a message: checked by their bytes, saved under safe new names, named in the message.</summary>
public class AttachmentsTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private static JsonObject Request(params (string Name, byte[] Bytes)[] images) => new()
    {
        ["images"] = new JsonArray([.. images.Select(i => (JsonNode?)new JsonObject { ["name"] = i.Name, ["base64"] = Convert.ToBase64String(i.Bytes) })]),
    };

    [Fact]
    public void AnImageIsSaved_UnderASafeName_AndTheMessageNamesItsPath()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-att-").FullName;
        try
        {
            var images = Attachments.Read(Request(("../../evil name?.jpg", Png)));
            var (name, _) = Assert.Single(images);
            Assert.Equal("evil-name.png", name); // the real kind decides the extension, and the name loses its path
            var paths = Attachments.Save(dir, images);
            var path = Assert.Single(paths);
            Assert.StartsWith(Path.Combine(dir, Attachments.Folder) + Path.DirectorySeparatorChar, path);
            Assert.Equal(Png, File.ReadAllBytes(path));
            var text = Attachments.WithImages("What is wrong here?", paths);
            Assert.StartsWith("What is wrong here?\n\nThe person attached an image", text);
            Assert.EndsWith("- " + path, text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WhatIsNotAnImage_OrTooMany_IsRefused()
    {
        Assert.Throws<RpcException>(() => Attachments.Read(Request(("notes.png", "hello"u8.ToArray()))));
        Assert.Throws<RpcException>(() => Attachments.Read(Request([.. Enumerable.Range(0, Attachments.MaxCount + 1).Select(i => ($"{i}.png", Png))])));
        Assert.Empty(Attachments.Read(new JsonObject()));
        Assert.Equal("Hi", Attachments.WithImages("Hi", []));
    }
}
