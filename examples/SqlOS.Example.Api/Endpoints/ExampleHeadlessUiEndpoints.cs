namespace SqlOS.Example.Api.Endpoints;

public static class ExampleHeadlessUiEndpoints
{
    public static void MapExampleHeadlessUiEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/authorize", (HttpContext httpContext) =>
        {
            var requestId = httpContext.Request.Query["request"].ToString();
            var view = httpContext.Request.Query["view"].ToString();
            var error = httpContext.Request.Query["error"].ToString();
            var email = httpContext.Request.Query["email"].ToString();
            var pendingToken = httpContext.Request.Query["pendingToken"].ToString();
            var displayName = httpContext.Request.Query["displayName"].ToString();

            return Results.Content(BuildHeadlessUiHtml(requestId, view, error, email, pendingToken, displayName), "text/html");
        }).ExcludeFromDescription();
    }

    private static string BuildHeadlessUiHtml(
        string requestId, string view, string error,
        string email, string pendingToken, string displayName)
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>SqlOS Headless Auth — Example</title>
            <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body { font-family: system-ui, -apple-system, sans-serif; background: #f8fafc; color: #0f172a; display: flex; justify-content: center; padding: 3rem 1rem; }
                .container { width: 100%; max-width: 420px; }
                h1 { font-size: 1.5rem; font-weight: 700; margin-bottom: .25rem; }
                .subtitle { color: #64748b; font-size: .875rem; margin-bottom: 1.5rem; }
                .error { background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; border-radius: 6px; padding: .75rem 1rem; font-size: .875rem; margin-bottom: 1rem; }
                .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 1.5rem; }
                label { display: block; font-size: .8125rem; font-weight: 500; color: #334155; margin-bottom: .25rem; }
                input { width: 100%; padding: .5rem .75rem; border: 1px solid #cbd5e1; border-radius: 6px; font-size: .875rem; margin-bottom: .75rem; }
                input:focus { outline: none; border-color: #2563eb; box-shadow: 0 0 0 2px rgba(37,99,235,.15); }
                button { width: 100%; padding: .625rem; background: #2563eb; color: #fff; border: none; border-radius: 6px; font-size: .875rem; font-weight: 600; cursor: pointer; margin-bottom: .5rem; }
                button:hover { background: #1d4ed8; }
                button.secondary { background: transparent; color: #2563eb; border: 1px solid #2563eb; }
                button.secondary:hover { background: #eff6ff; }
                .toggle { text-align: center; margin-top: .75rem; font-size: .8125rem; color: #64748b; }
                .toggle a { color: #2563eb; cursor: pointer; text-decoration: none; }
                #result { margin-top: 1rem; font-size: .8125rem; white-space: pre-wrap; background: #f1f5f9; border-radius: 6px; padding: 1rem; display: none; }
                .info { background: #f0f9ff; color: #0c4a6e; border: 1px solid #bae6fd; border-radius: 6px; padding: .75rem 1rem; font-size: .8125rem; margin-bottom: 1rem; }
            </style>
        </head>
        <body>
        <div class="container">
            <h1>SqlOS Headless Auth</h1>
            <p class="subtitle">Example stub UI — your app replaces this page with its own design.</p>

            <div class="info">
                This page is served by the SqlOS example API as a self-contained demo of headless auth.
                In production, <code>BuildUiUrl</code> points to your own frontend.
            </div>

            {{(string.IsNullOrEmpty(error) ? "" : $"""<div class="error">{System.Net.WebUtility.HtmlEncode(error)}</div>""")}}

            <div class="card">
                <!-- Login form -->
                <div id="login-form" style="display:none">
                    <label for="login-email">Email</label>
                    <input id="login-email" type="email" value="{{System.Net.WebUtility.HtmlEncode(email)}}" placeholder="you@example.com" />
                    <label for="login-password">Password</label>
                    <input id="login-password" type="password" placeholder="Password" />
                    <button onclick="doLogin()">Sign in</button>
                    <div class="toggle"><a onclick="showView('signup')">Create an account</a></div>
                </div>

                <!-- Signup form -->
                <div id="signup-form" style="display:none">
                    <label for="signup-name">Display Name</label>
                    <input id="signup-name" type="text" value="{{System.Net.WebUtility.HtmlEncode(displayName)}}" placeholder="Jane Doe" />
                    <label for="signup-email">Email</label>
                    <input id="signup-email" type="email" value="{{System.Net.WebUtility.HtmlEncode(email)}}" placeholder="you@example.com" />
                    <label for="signup-password">Password</label>
                    <input id="signup-password" type="password" placeholder="Choose a password" />
                    <label for="signup-org">Organization Name</label>
                    <input id="signup-org" type="text" placeholder="Acme Inc." />
                    <label for="signup-first">First Name</label>
                    <input id="signup-first" type="text" placeholder="Jane" />
                    <label for="signup-last">Last Name</label>
                    <input id="signup-last" type="text" placeholder="Doe" />
                    <button onclick="doSignup()">Create Account</button>
                    <div class="toggle"><a onclick="showView('login')">Already have an account?</a></div>
                </div>

                <!-- Identify form (default) -->
                <div id="identify-form" style="display:none">
                    <label for="identify-email">Email</label>
                    <input id="identify-email" type="email" value="{{System.Net.WebUtility.HtmlEncode(email)}}" placeholder="you@example.com" />
                    <button onclick="doIdentify()">Continue</button>
                    <div class="toggle"><a onclick="showView('signup')">Create an account</a></div>
                </div>
            </div>

            <pre id="result"></pre>
        </div>

        <script>
            const REQUEST_ID = '{{System.Net.WebUtility.HtmlEncode(requestId)}}';
            const API_BASE = '/sqlos/auth/headless';
            const initialView = '{{System.Net.WebUtility.HtmlEncode(string.IsNullOrEmpty(view) ? "login" : view)}}';

            function showView(v) {
                document.getElementById('login-form').style.display = 'none';
                document.getElementById('signup-form').style.display = 'none';
                document.getElementById('identify-form').style.display = 'none';
                const target = document.getElementById(v + '-form') || document.getElementById('login-form');
                target.style.display = 'block';
            }

            function showResult(data) {
                const el = document.getElementById('result');
                el.style.display = 'block';
                el.textContent = JSON.stringify(data, null, 2);
            }

            async function post(path, body) {
                const res = await fetch(API_BASE + path, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                });
                const data = await res.json();
                if (data.type === 'redirect' && data.redirectUrl) {
                    window.location.href = data.redirectUrl;
                    return;
                }
                showResult(data);
                if (data.view) showView(data.view);
            }

            async function doIdentify() {
                await post('/identify', {
                    requestId: REQUEST_ID,
                    email: document.getElementById('identify-email').value
                });
            }

            async function doLogin() {
                await post('/password/login', {
                    requestId: REQUEST_ID,
                    email: document.getElementById('login-email').value,
                    password: document.getElementById('login-password').value
                });
            }

            async function doSignup() {
                await post('/signup', {
                    requestId: REQUEST_ID,
                    displayName: document.getElementById('signup-name').value,
                    email: document.getElementById('signup-email').value,
                    password: document.getElementById('signup-password').value,
                    organizationName: document.getElementById('signup-org').value,
                    customFields: {
                        firstName: document.getElementById('signup-first').value,
                        lastName: document.getElementById('signup-last').value
                    }
                });
            }

            showView(initialView);
        </script>
        </body>
        </html>
        """;
    }
}
