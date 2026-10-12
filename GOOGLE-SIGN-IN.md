# Optional Google sign-in

Aurora Translate works without an account or a translation API key. Google sign-in is optional and saves your account name and email on this PC. It does not sync translations, themes, or history to Google.

Google requires a registered OAuth application to show a real sign-in screen. This download does not come with someone else's Google Cloud credentials. A one-time setup is needed for your own Google sign-in:

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create or select a project.
2. Open **Google Auth Platform** and complete the **Branding** setup. Give the application a name such as “Aurora Translate” and supply your support/contact email.
3. Configure **Audience**. For a personal Google account, use **External**. If the app is in **Testing**, add your Google email under **Test users** if Google requires it. An organization account may have administrator restrictions.
4. Open **Clients → Create client**. Choose **Desktop app**, give it a name, and create it. Download that client's JSON file. A **Web application** client or service-account file will not work.
5. In Aurora Translate, open **Settings**, use the Google OAuth JSON import button, select the downloaded JSON, and choose **Sign in with Google**.
6. Complete sign-in in your normal browser and allow the basic name, email, and profile permissions. Return to Aurora Translate when the browser says approval was received. Sign-in times out after three minutes; retry if you need more time.

The app opens your default browser. It does not ask you to type your Google password into Aurora Translate. The callback runs only on `127.0.0.1` at a temporary port. Desktop sign-in uses PKCE and a random request state; no website or fixed redirect port needs to be hosted.

Imported client configuration, OAuth tokens, and the cached profile are encrypted for your current Windows account in `%LOCALAPPDATA%\AuroraTranslate`. The original JSON you downloaded is still in its original location. Keep that download private or remove it after importing. A displayed saved identity is a local cache; Google authorization is verified online when you sign in again.

**Sign out** removes the saved Google tokens/profile from this PC and attempts to revoke Google's authorization. If you sign out while offline, you can also remove the app under [Google Account connections](https://myaccount.google.com/connections). The imported Desktop client remains so that you can sign in again.

If Google shows `invalid_client` or `redirect_uri_mismatch`, confirm that you created a **Desktop app** client and import its freshly downloaded JSON. If Google blocks a testing app, check its audience, test users, and any organization administrator rules.

Protocol details: [Google's desktop OAuth guide](https://developers.google.com/identity/protocols/oauth2/native-app), [Google Auth Platform setup](https://developers.google.com/identity/protocols/oauth2/production-readiness/brand-verification), and [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect).
