commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.App/ViewModels/AccountsViewModel.cs b/src/DongGfx.App/ViewModels/AccountsViewModel.cs
index 147b97b..84cf746 100644
--- a/src/DongGfx.App/ViewModels/AccountsViewModel.cs
+++ b/src/DongGfx.App/ViewModels/AccountsViewModel.cs
@@ -590,6 +590,28 @@ public sealed partial class AccountsViewModel : ObservableObject
         return added;
     }
 
+    /// <summary>Terminal-tab sign-in seam: import a pasted PAT through the
+    /// same one-token-per-row discovery path as the Accounts tab, then
+    /// connect the row so the Terminal's account bar goes live immediately.
+    /// Returns the number of rows added (0 = token already imported).</summary>
+    public async Task<int> ImportTokenFromTerminalAsync(string token)
+    {
+        var added = await ImportTokenAsAccountsAsync(token, "Terminal (PAT)").ConfigureAwait(true);
+        if (added > 0)
+        {
+            var row = _hub.Accounts.FirstOrDefault(a => a.Config.ApiToken == token);
+            if (row is not null)
+            {
+                _ = row.ConnectAsync(); // background — bar reflects state via StateChanged
+            }
+        }
+        return added;
+    }
+
+    /// <summary>Terminal-tab OAuth seam: the same browser PKCE flow the
+    /// Accounts tab drives, so "Sign in with Deriv" works from either tab.</summary>
+    public Task SignInFromTerminalAsync() => SignInWithDerivAsync();
+
     [RelayCommand]
     private void ImportAccounts()
     {
