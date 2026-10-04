# Google OAuth setup

Gmail Organiser talks to Gmail through **your own** Google Cloud project and OAuth client. Nothing is shared with
anyone else: the client ID and secret stay on your machine (in `.env` or encrypted in the local database), and the
refresh token is stored encrypted with ASP.NET Data Protection.

You need a Google account and about ten minutes. Google renames console menus from time to time; if a label below
doesn't match exactly, look for the closest equivalent.

## 1. Create a Google Cloud project

1. Open the [Google Cloud console](https://console.cloud.google.com/).
2. Use the project picker at the top → **New project**.
3. Give it any name (for example `gmail-organiser`) and create it. Make sure it is selected afterwards.

## 2. Enable the Gmail API

1. Go to **APIs & Services → Library**.
2. Search for **Gmail API**, open it and click **Enable**.

## 3. Configure the OAuth consent screen

1. Go to **APIs & Services → OAuth consent screen** (newer consoles call this **Google Auth Platform → Branding /
   Audience / Data access**).
2. User type / audience: **External**.
3. App name: anything you like. Support and developer contact email: your own address.
4. Scopes (**Data access → Add or remove scopes**): add exactly these three:

   | Scope | Used for |
   |---|---|
   | `https://www.googleapis.com/auth/gmail.modify` | read mail, add/remove labels, archive, move to Trash |
   | `https://www.googleapis.com/auth/gmail.settings.basic` | create Gmail filters |
   | `https://www.googleapis.com/auth/gmail.labels` | create and manage labels |

   Do **not** add the full-mail scope `https://mail.google.com/`. It allows permanent deletion, which this app never
   does: deletable mail is only labelled `To-Be-Deleted`, and "Delete" moves it to Trash, where Google keeps it for
   30 days. `gmail.modify` is enough for that and cannot bypass the Trash.
5. Test users: while the app is in *Testing*, add your own Gmail address. (This stops mattering once you publish it in
   step 6.)

## 4. Create the OAuth client

1. Go to **APIs & Services → Credentials → Create credentials → OAuth client ID** (or **Google Auth Platform →
   Clients → Create client**).
2. Application type: **Web application**.
3. **Authorised redirect URIs**: add **both** of these:

   | Run mode | Redirect URI |
   |---|---|
   | Development from the IDE (`ng serve` on 4200) | `http://localhost:4200/api/auth/google/callback` |
   | Docker compose (`WEB_PORT`, default 5180) | `http://localhost:5180/api/auth/google/callback` |

   If you set a different `WEB_PORT` in `.env`, also set `APP_BASE_URL` to the same port (for example
   `WEB_PORT=5280` and `APP_BASE_URL=http://localhost:5280`) and register that port instead of 5180. The app builds
   the redirect URI as `{APP_BASE_URL}/api/auth/google/callback`, not from `WEB_PORT`; if the two disagree, Google
   returns `redirect_uri_mismatch`. The URI must match character for character (`localhost`, not `127.0.0.1`;
   `http`, no trailing slash).
4. No *Authorised JavaScript origins* are needed.
5. Click **Create** and copy the **Client ID** and **Client secret**.

## 5. Enter the client ID and secret

Pick one:

- **Setup wizard (recommended).** Open the app and enter both values in the setup wizard. The
  secret is stored encrypted in the local database.
- **`.env` (compose).** Set them in your `.env`:

  ```dotenv
  GOOGLE_CLIENT_ID=1234567890-abc.apps.googleusercontent.com
  GOOGLE_CLIENT_SECRET=your-client-secret
  ```

  Then restart the stack: `docker compose up -d`. Setting **either** value in `.env` locks
  **both** Google client fields in the UI, and both are then read from `.env` only. So set both in `.env`, or
  neither (and use the wizard); setting only the ID leaves the secret missing and Connect fails.

From the IDE, `dotnet run` does not read `.env`: use the wizard, or set `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`
in your local run configuration's environment (never in the committed `launchSettings.json`). Note that the
`GmailOrganiser.Api` launch profile sets `GMAIL_FAKE=true`, which uses a synthetic in-memory mailbox; set
`GMAIL_FAKE=false` in your local run configuration to connect the real account.

Then connect Gmail from the setup wizard, sign in, and allow the three permissions. Later, **Settings → Gmail
connection** shows the connected account, with **Reconnect** and **Disconnect**.

## 6. Publish the app to "In production"

While the consent screen is in **Testing**, Google expires refresh tokens after **7 days**, so you would have to
reconnect every week. To avoid that:

1. Go to **OAuth consent screen** (or **Google Auth Platform → Audience**).
2. Under *Publishing status*, click **Publish app** and confirm. The status becomes **In production**.
3. You do **not** need to submit the app for verification. An unverified app is fine for personal use (Google caps
   unverified apps at 100 users, and you are the only one).
4. Reconnect Gmail once after publishing, so the new refresh token is issued under the production status.

Because the app is unverified, Google shows a warning when you connect: **"Google hasn't verified this app"**. That
is expected for your own project. Click **Advanced → Go to *&lt;your app name&gt;* (unsafe)**, then allow the
permissions. Check that the app name is the one you created, and that the account shown is your own.

## Troubleshooting

| What you see | Cause | Fix |
|---|---|---|
| Google error page `Error 400: redirect_uri_mismatch` | The redirect URI the app sent isn't registered on the client. | Add the exact URI from step 4 for the way you're running (4200 IDE, 5180 or your `WEB_PORT` compose). Check `APP_BASE_URL` if you changed it. Changes can take a few minutes to apply. |
| The app reports `missing_scopes` after connecting | You unticked a permission on the consent screen, or a scope is missing from step 3. | Make sure all three scopes are added in the console, then connect again and leave every permission ticked. Nothing is stored until all three are granted. |
| The app reports `reauth_required` | Google refused the stored refresh token (`invalid_grant`): it was revoked, the password changed, or the app was still in *Testing* and the 7-day limit passed. | Publish the app (step 6) if you haven't, then press **Connect Gmail** under **Settings → Gmail connection**. |
| `access_denied` | You clicked **Cancel** on the Google screen, or your address isn't a test user while the app is in *Testing*. | Add yourself as a test user or publish the app, then connect again. |
| `state_mismatch` | The sign-in took too long or was started in another tab. | Start connecting again from the same tab. |
| `exchange_failed` | Google rejected the code exchange, usually a wrong client secret. | Re-enter the client ID and secret (wizard or `.env`) and try again. |

To revoke access at any time: [Google Account → Security → Third-party apps](https://myaccount.google.com/connections),
or **Disconnect** under **Settings → Gmail connection**.
