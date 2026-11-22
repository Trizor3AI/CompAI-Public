using System.Text;
using CompAI;

var directory = Path.Combine(Path.GetTempPath(), "compai-attachments-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
void Check(bool valid) { if (!valid) throw new Exception("Attachment fixture failed"); checks++; }
void Rejected(string name, byte[] bytes)
{
    var path = Path.Combine(directory, name); File.WriteAllBytes(path, bytes);
    try { ChatAttachment.ReadSelected(path); throw new Exception("Expected attachment rejection"); }
    catch (InvalidDataException) { checks++; }
}
try
{
    var path = Path.Combine(directory, "selected.txt");
    File.WriteAllText(path, "first", new UTF8Encoding(false));
    var selected = ChatAttachment.ReadSelected(path);
    File.WriteAllText(path, "changed", new UTF8Encoding(false));
    Check(Encoding.UTF8.GetString(Convert.FromBase64String(selected.Base64)) == "first");
    File.Delete(path);
    Check(selected.Name == "selected.txt" && !ChatAttachment.Metadata(new[] { selected }).Contains(directory));
    var original = new[] { selected };
    var frozen = ChatAttachment.Freeze(original);
    original[0] = null!;
    Check(ReferenceEquals(frozen[0], selected));
    Rejected("binary.txt", new byte[] { 255 });
    Rejected("control.txt", new byte[] { 0 });
    Rejected("bad.json", Encoding.UTF8.GetBytes("not JSON"));
    Rejected("deep.json", Encoding.UTF8.GetBytes(new string('[', 33) + new string(']', 33)));
    Rejected("large.txt", new byte[ChatAttachment.MaxTextBytes + 1]);
    Rejected("unsupported.zip", new byte[] { 1 });
    Rejected("fake.png", Encoding.UTF8.GetBytes("not an image"));
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    var imagePath = Path.Combine(directory, "synthetic.png"); File.WriteAllBytes(imagePath, png);
    Check(ChatAttachment.ReadSelected(imagePath).IsImage);
    using (var loader = new ChatAttachmentLoader())
    using (var releaseRead = new ManualResetEventSlim())
    {
        var owner = "";
        Check(loader.Start("first", token =>
        {
            releaseRead.Wait(TimeSpan.FromSeconds(5), token);
            return ChatAttachment.ReadSelected(imagePath);
        }, (id, attachment) => { owner = id; return attachment.IsImage ? null : "Wrong type"; }) is null);
        Check(loader.Pending("first") && !loader.Pending("second"));
        Check(loader.Start("second", _ => selected, (_, _) => null) == ChatAttachmentLoader.BusyMessage);
        releaseRead.Set();
        Check(SpinWait.SpinUntil(() => !loader.Pending("first"), 5000) && owner == "first");
        Check(loader.Start("second", _ => throw new FileNotFoundException(path), (_, _) => null) is null);
        Check(SpinWait.SpinUntil(() => !loader.Pending("second"), 5000));
        Check(loader.TakeError("first") is null && loader.TakeError("second") is { } safe && !safe.Contains(path));
        Check(loader.TakeError("second") is null);
    }
    using (var loader = new ChatAttachmentLoader())
    using (var started = new ManualResetEventSlim())
    {
        var committed = false;
        Check(loader.Start("first", token =>
        {
            started.Set();
            SpinWait.SpinUntil(() => token.IsCancellationRequested, 5000);
            return selected;
        }, (_, _) => { committed = true; return null; }) is null);
        Check(started.Wait(5000));
        loader.Dispose();
        Check(SpinWait.SpinUntil(() => !loader.Pending("first"), 5000) && !committed);
    }
    png[40] ^= 1; Rejected("bad-crc.png", png);
    try { ChatAttachment.Freeze(new[] { selected, selected, selected, selected }); throw new Exception("Expected count rejection"); }
    catch (InvalidDataException) { checks++; }
    checks += ClipboardImageChecks.Run();
    Console.WriteLine($"{checks} attachment checks passed");
}
finally { Directory.Delete(directory, true); }
