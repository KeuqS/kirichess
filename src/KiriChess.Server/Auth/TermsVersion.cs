namespace KiriChess.Server.Auth;

/// <summary>
/// The version of the Terms and Privacy Policy currently in force (Phase 8a.2). A plain date string.
///
/// This date lives in FIVE places and they must move together: this constant, the `Last updated` /
/// `Version` line in docs/legal/TERMS.md and in docs/legal/PRIVACY.md, and the two hand-maintained
/// copies of those documents at ui/app/terms/page.tsx and ui/app/privacy/page.tsx. The .tsx pages are
/// duplicated prose, not renders of the Markdown, so nothing catches the drift. This comment used to
/// say "all three" and undercounted its own list; the 8a.2 transfer-disclosure edit then bumped
/// PRIVACY.md alone, leaving the deployed page a version behind and missing a whole section while new
/// registrations were stamped with a version string that matched no document anyone could read.
///
/// <c>LegalDocumentDriftTests</c> now fails the build on exactly that, on both halves of it: every
/// copy's date must equal this constant, and the numbered sections of each Markdown document must
/// exist on its page. The duplication is still real, the drift is no longer silent. Rendering the
/// Markdown instead of duplicating it would remove the need for the guard entirely, and remains the
/// right fix whenever the pages are next touched.
///
/// Stamped into ApplicationUser.TermsVersion at registration so a future re-consent pass can find
/// everyone still on an older version. Nothing reads it back yet; that interstitial is V2 (README
/// out-of-scope).
/// </summary>
public static class TermsVersion
{
    public const string Current = "2026-10-11";
}
