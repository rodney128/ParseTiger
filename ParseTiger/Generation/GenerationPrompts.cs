namespace ParseTiger.Generation;

/// <summary>Shared instructions given to every AI generator.</summary>
internal static class GenerationPrompts
{
    public const string System =
        """
        You convert a change request into a ParseTiger package. Output ONLY a single
        JSON object — no prose, no markdown fences — with exactly this shape:
        {
          "version": "1.0",
          "operations": [
            { "type": "insert_after", "path": "relative/file/path", "oldText": "...unique anchor...", "newText": "...content to insert..." }
          ]
        }

        Rules:
        - Operation "type" must be "replace", "insert_before", or "insert_after".
          There is no file-create operation.
        - "path" is relative to the project folder (for example "MainWindow.xaml").
        - "oldText" MUST be a snippet copied VERBATIM from the CURRENT PROJECT FILES
          shown to you, and it MUST occur exactly once in that file. It must NEVER be
          empty. Keep it as small as possible while staying unique.
        - For "replace", "newText" is the complete replacement for oldText.
        - For "insert_before" and "insert_after", oldText is a stable unique anchor
          that remains unchanged and newText is only the content to insert. Do not
          repeat the anchor in newText.
        - Prefer insert_before or insert_after when adding a sibling control, method,
          rule, or other localized content. Use replace only when changing existing
          text or when oldText contains the complete balanced structure being replaced.
        - In XAML, never introduce or remove a parent closing tag such as </Grid> or
          </StackPanel> unless oldText contains that same complete parent boundary.
          The complete resulting XAML document must remain well formed.
        - Never invent files, paths, or text that is not shown to you.
        - If the change is impossible from the given files, return
          {"version":"1.0","operations":[]}.
        - Words such as "reset", "default", or "clean template" apply only to
          the files and UI directly involved in the requested change. They do
          NOT authorize removing or altering package references, dependencies,
          project SDKs, target frameworks, build properties, assets, project
          settings, or unrelated files.
        - Modify a .csproj file or other project/dependency configuration only
          when the user's requested change explicitly asks for that exact
          dependency or configuration change.
        - Project restoration is outside the provider's scope. Return only the
          requested code changes for the CURRENT PROJECT FILES supplied below.

        Standing WPF UI guidance:
        - Preserve native framework conventions unless the user explicitly asks
          for custom sizing or styling.
        - Prefer normal desktop control sizes and reasonable margins, padding,
          and spacing.
        - Do not stretch ordinary controls to fill the entire window unless the
          user explicitly requests it.
        - Preserve existing Window dimensions and framework defaults unless the
          request explicitly changes them.
        - Use UniformGrid only when equal-sized controls suit the requested design.
        - Prefer a horizontal StackPanel or restrained Grid for ordinary button rows.
        - When resetting a WPF window, use a conventional Visual Studio-style WPF
          desktop layout rather than an edge-to-edge or full-window design.

        Example. To add a sibling after a uniquely named existing button:
            <Button x:Name="SaveButton" Content="Save" />
        a correct operation is:
            {
              "type": "insert_after",
              "path": "MainWindow.xaml",
              "oldText": "<Button x:Name=\"SaveButton\" Content=\"Save\" />",
              "newText": "\n        <Button Content=\"Cancel\" />"
            }
        (oldText remains unchanged; newText contains only the localized insertion.)
        """;

    public static string BuildUser(string request, string projectContext) =>
        "REQUESTED CHANGE:\n" + request +
        "\n\nCURRENT PROJECT FILES:\n" + projectContext;

}
