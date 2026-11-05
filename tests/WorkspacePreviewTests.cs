using System.Reflection;
using Microsoft.Web.WebView2.Core;
using SteamVrTranslator;

internal static class WorkspacePreviewTests
{
    internal static async Task RunAsync(MainForm form, CoreWebView2 core)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var push = typeof(MainForm).GetMethod("PushWorkspace", flags)!;
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
        var preview = Field<PictureBox>("preview");
        var label = Field<Control>("imageBox");
        var status = Field<Label>("status");
        var cancel = Field<Button>("cancelButton");
        var originalEncoder = form.EncodeWorkspaceThumbnail;
        var originalImage = preview.Image;
        var originalLabel = label.Text;
        bool originalCancel = cancel.Enabled;
        int calls = 0, ui = Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        void Update(bool full = false) => push.Invoke(form, [full]);
        void Replace(Bitmap? source, string caption)
        {
            var old = preview.Image;
            preview.Image = source;
            if (!ReferenceEquals(old, originalImage)) old?.Dispose();
            label.Text = caption;
            Update();
        }
        async Task WaitFor(string script)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await core.ExecuteScriptAsync(script) != "true") await Task.Delay(16, deadline.Token);
        }

        form.EncodeWorkspaceThumbnail = image =>
        {
            if (Environment.CurrentManagedThreadId == ui) throw new Exception("Thumbnail resized on the UI thread");
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The preview worker could not be released");
                if (image.Width != 3840) throw new Exception("The worker lost its owned image when the displayed preview was replaced");
            }
            return MainForm.EncodeThumbnail(image);
        };
        try
        {
            await core.ExecuteScriptAsync("window.previewEvents=[];window.chrome.webview.addEventListener('message',e=>{if(e.data.preview)window.previewEvents.push(e.data.captureLabel);})");
            Replace(new Bitmap(3840, 2160), "fixture frame #1");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Replace(new Bitmap(64, 64), "fixture frame #2");
            Replace(new Bitmap(1920, 1080), "fixture frame #3");
            cancel.Enabled = true;
            status.Text = "状态更新仍可取消";
            Update();
            await WaitFor("document.body.innerText.includes('状态更新仍可取消') && !document.querySelector('#cancelTask').disabled");
            if (calls != 1 || await core.ExecuteScriptAsync("document.querySelector('#captureLabel').textContent.includes('#3')") == "true")
                throw new Exception("A busy encoder must coalesce frames without advertising new pixels too early");
            release.Set();
            await WaitFor("document.querySelector('#previewImage').naturalWidth === 720 && document.querySelector('#previewImage').naturalHeight === 405 && document.querySelector('#captureLabel').textContent === 'fixture frame #3'");
            for (int i = 0; i < 20; i++) { status.Text = "preview fixture status " + i; Update(); }
            await WaitFor("document.body.innerText.includes('preview fixture status 19') && window.previewEvents.length === 1");
            Update(full: true);
            await WaitFor("window.previewEvents.length === 2 && window.previewEvents.every(s=>s==='fixture frame #3')");
            if (calls != 2) throw new Exception("Only the first and latest bitmap should be encoded, including after a full WebView snapshot");
            Console.WriteLine("PASS WebView preview: 3 synthetic captures, 2 background encodes, 1 latest-frame delivery, 0 encodes for 20 status updates and reload");
        }
        finally
        {
            release.Set();
            form.EncodeWorkspaceThumbnail = originalEncoder;
            var last = preview.Image;
            preview.Image = originalImage;
            if (!ReferenceEquals(last, originalImage)) last?.Dispose();
            cancel.Enabled = originalCancel;
            label.Text = originalLabel;
            Update();
        }
    }
}
