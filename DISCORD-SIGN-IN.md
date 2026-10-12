# Discord sign-in for Aurora Translate

Discord sign-in displays your Discord name and profile image in Aurora Translate. The translator and AI conversation work without signing in.

The app needs a **registered Discord Application ID** before the sign-in button can connect. This ID is public application information; it is not an API key. An unregistered desktop executable cannot create its own Discord sign-in app. The Google button in the earlier version also needed a registered Google OAuth app, which is why it was not ready to use immediately.

## One-time setup

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) in your browser and sign in to Discord there.
2. Choose **New Application**, name it **Aurora Translate**, and create it.
3. On **General Information**, copy its numeric **Application ID**.
4. Open the app's **OAuth2** settings. Enable **Public Client** and save. This allows the desktop app to use PKCE instead of storing a client secret. **Public Client** is an OAuth setting; **Public Bot** is a different setting.
5. Under **Redirects**, add this exact URL and save:

   ```text
   http://127.0.0.1:43821/callback
   ```

6. In Aurora Translate, open **Settings**, paste the **Application ID** into its Discord field, and save it.
7. Choose **Sign in with Discord**. Your usual browser opens Discord's consent page. Approve access to your basic profile, then return to Aurora Translate. Complete this within three minutes.

Do not paste your Discord password, a user token, a bot token, or the app's client secret into Aurora Translate. They are not needed and the app does not accept them. No bot or server installation is needed for this profile sign-in.

## Your profile image

After approval, Aurora Translate requests your basic profile using the `identify` permission and downloads the image from Discord's CDN. An animated Discord avatar is shown as a still PNG. If your account has no custom avatar, Discord's default avatar is shown. If the image cannot be downloaded, the app can still sign you in and display your name.

Your OAuth session is encrypted for your current Windows user and saved locally. Your app ID and cached profile stay on this computer; this feature does not synchronize translation history or AI conversations. Choose **Sign out** to remove the saved session and its cached avatar. To remove Discord's authorization too, go to Discord **User Settings → Authorized Apps → Aurora Translate → Deauthorize**, especially if you signed out while offline.

## If sign-in fails

- **The Discord settings have not been saved:** paste the Application ID from General Information, then save it in Aurora Translate.
- **Discord rejects the application:** check that **Public Client** is enabled under OAuth2. A client secret is intentionally not used by this desktop app.
- **Invalid redirect:** copy the exact URL above. `localhost`, another port, `/`, and a trailing slash are different redirect URLs.
- **The local sign-in port is busy:** close a second Aurora Translate window and retry.
- **Timed out or declined:** select Sign in again and complete the browser approval within three minutes.
- **Your browser cannot open:** set your default web browser in Windows Settings.
- **Offline or Discord is unavailable:** the app's translation and local AI features remain available; try sign-in again when connected.

The implementation and local protocol checks are included. A complete live Discord approval can only be verified after a real application has been registered and configured.

## Official references

- [Discord: Public Client token exchange, PKCE and refresh](https://docs.discord.com/developers/discord-social-sdk/development-guides/account-linking-with-discord#token-exchange-for-public-clients)
- [Discord: OAuth2 scopes, state, authorization and revocation](https://docs.discord.com/developers/topics/oauth2)
- [Discord: Current user profile](https://docs.discord.com/developers/resources/user#get-current-user)
- [Discord: Avatar CDN URLs and default-avatar rules](https://docs.discord.com/developers/reference#image-formatting)
