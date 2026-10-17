using Android.App;
using Android.Content;
using Android.Content.PM;

namespace StravaReports;

/// <summary>Receives the stravareports://oauth redirect for WebAuthenticator on Android.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "stravareports", DataHost = "oauth")]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}
