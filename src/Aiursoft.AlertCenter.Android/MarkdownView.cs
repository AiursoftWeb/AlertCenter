using System.Text.Json;
using Android.Content;
using Android.Webkit;
using Android.Widget;
using Android.Graphics;

namespace Aiursoft.AlertCenter.Android;

// Only the shared, bundled renderer runs here. Incident text is JSON data, never executable HTML.
// The reader has no cookie/token bridge or filesystem access. Remote images require an explicit tap.
internal sealed class MarkdownView : WebView
{
    private static string? _renderer;

    public MarkdownView(Context context, string markdown, bool dark, string server) : base(context)
    {
        SetBackgroundColor(Color.Transparent);
        Settings.JavaScriptEnabled = true;
        Settings.AllowFileAccess = false;
        Settings.AllowContentAccess = false;
        Settings.BlockNetworkLoads = false;
        Settings.DomStorageEnabled = false;
        Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        Settings.TextZoom = (int)(100 * context.Resources!.Configuration!.FontScale);
        VerticalScrollBarEnabled = false;
        HorizontalScrollBarEnabled = false;
        if (_renderer == null)
        {
            using var reader = new StreamReader(context.Assets!.Open("markdown/uistack-markdown-ui.js"));
            _renderer = reader.ReadToEnd();
        }
        SetWebViewClient(new ReaderClient(context));
        SetWebChromeClient(new ReaderChromeClient(this));
        CookieManager.Instance?.SetAcceptThirdPartyCookies(this, false);
        var foreground = dark ? "#E9E6F2" : "#202035";
        var background = dark ? "#2B2937" : "#F3F3FA";
        var accent = dark ? "#BDB2FF" : "#5946D2";
        var nonce = Guid.NewGuid().ToString("N");
        var html = $$"""
            <!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-{{nonce}}'; style-src 'unsafe-inline'; img-src https: data:; base-uri 'none'; form-action 'none'">
            <style>
            html,body{margin:0;padding:0;background:transparent;color:{{foreground}};font:16px/1.6 sans-serif;overflow-wrap:anywhere}
            p{margin:0 0 12px}h1,h2,h3{font-size:1.15em;line-height:1.4;margin:12px 0 8px}
            a{color:{{accent}}}button{font:inherit;color:{{accent}};background:{{background}};border:0;border-radius:8px;padding:12px;text-align:left;min-height:48px}pre,blockquote{background:{{background}};padding:12px;border-radius:8px;margin:8px 0;overflow:auto}
            code{font-family:monospace;font-size:.88em}table{display:block;overflow:auto;border-collapse:collapse;max-width:100%}
            td,th{padding:8px;border:1px solid {{background}}}img{max-width:100%;height:auto}ul,ol{padding-left:24px}
            </style></head><body><main id="content"></main>
            <script nonce="{{nonce}}">{{_renderer}}</script>
            <script nonce="{{nonce}}">
            const template=document.createElement('template');
            template.innerHTML=AiursoftMarkdownUi.renderMarkdown({{JsonSerializer.Serialize(markdown)}});
            for(const image of template.content.querySelectorAll('img')) {
                const source=image.getAttribute('src'); image.removeAttribute('src'); image.removeAttribute('srcset');
                const button=document.createElement('button'); button.textContent={{JsonSerializer.Serialize(UiText.Get("Load image"))}}+' · '+(image.alt||{{JsonSerializer.Serialize(UiText.Get("Attachment"))}});
                button.onclick=()=>{image.src=source;button.remove();};
                image.before(button);
            }
            document.getElementById('content').append(template.content);
            new ResizeObserver(()=>document.title='height:'+Math.ceil(document.getElementById('content').getBoundingClientRect().height)).observe(document.getElementById('content'));
            </script>
            </body></html>
            """;
        LoadDataWithBaseURL(server.TrimEnd('/') + "/", html, "text/html", "UTF-8", null);
    }

    private sealed class ReaderChromeClient(WebView view) : WebChromeClient
    {
        public override void OnReceivedTitle(WebView? sender, string? title)
        {
            if (title?.StartsWith("height:", StringComparison.Ordinal) == true)
                new HeightCallback(view).OnReceiveValue(new Java.Lang.String(title[7..]));
        }
    }
    private sealed class ReaderClient(Context context) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            if (url?.Scheme is "https" or "http")
            {
                try { context.StartActivity(new Intent(Intent.ActionView, url)); }
                catch (ActivityNotFoundException) { }
            }
            return true;
        }
        public override void OnPageFinished(WebView? view, string? url)
        {
            view?.EvaluateJavascript("Math.ceil(document.getElementById('content').getBoundingClientRect().height)", new HeightCallback(view));
        }
    }
    private sealed class HeightCallback(WebView view) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            if (!int.TryParse(value?.ToString(), out var height)) return;
            var density = view.Resources!.DisplayMetrics!.Density;
            view.LayoutParameters = new LinearLayout.LayoutParams(-1, Math.Max(1, (int)((height + 8) * density)));
        }
    }
}
