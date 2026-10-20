namespace OpenTail.Modules.OpenApi;

/// <summary>The result of checking a profile without a Host: what it exposes and what it should fix.</summary>
public sealed record ProfileReport(string Name, string? Version, IReadOnlyList<ProfileReportTool> Tools, IReadOnlyList<string> Findings);

public sealed record ProfileReportTool(string Name, ToolRisk Risk, bool ReadsUntrusted, bool Preview, string Description, string Schema);

/// <summary>
/// Conformance for integration profiles, for authors and CI: <c>ProfileConformance.Check("profiles/x/x.json")</c> loads the profile and its
/// spec exactly as the Host would (every load-time error throws, with the reason), and reports each tool the model would see and every
/// finding (a door left open, a missing description, an untrusted read without a Response rule). A profile ready to ship has no findings.
/// Nothing is sent anywhere; no credentials are needed.
/// </summary>
public static class ProfileConformance
{
    /// <param name="reference">A profile file, or <c>package:Assembly/name</c> for one in a referenced package.</param>
    /// <param name="settings">Values for the profile's own Config settings (the operator's lists), if a check depends on them.</param>
    public static ProfileReport Check(string reference, string? baseDirectory = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? settings = null)
    {
        var profile = OpenApiProfile.Open(reference, baseDirectory ?? Directory.GetCurrentDirectory());
        var module = OpenApiModule.Create(profile.ReadRelative(profile.Spec), new OpenApiModuleOptions
        {
            Namespace = profile.Name.Replace('.', '_').Replace(' ', '_').ToLowerInvariant(),
            Profile = profile,
            ProfileSettings = settings ?? new Dictionary<string, IReadOnlyList<string>>(),
            AllowInsecureHttp = true,
            HttpClient = new HttpClient(new Refuse()),
        });
        var capture = new Capture();
        module.Register(capture);
        return new ProfileReport(profile.Name, profile.Version, capture.Tools, module.ProfileFindings);
    }

    private sealed class Capture : IToolRegistry
    {
        public List<ProfileReportTool> Tools { get; } = [];
        public void Add(ToolDescriptor d, AIFunction f, IPreviewableTool? preview = null) =>
            Tools.Add(new ProfileReportTool(d.Name, d.Risk, d.ReadsUntrusted, preview is not null, d.Description, f.JsonSchema.GetRawText()));
    }

    /// <summary>A checker never calls the API.</summary>
    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("A conformance check does not call the API.");
    }
}
