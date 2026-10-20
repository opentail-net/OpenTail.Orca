[assembly: HostModuleFactory(typeof(OpenTail.Modules.Skills.SkillsModuleFactory))]

namespace OpenTail.Modules.Skills;

/// <summary><c>"Type": "skills"</c> in modules.json. Config: <c>{ "Store": "skills-store", "Label": "production", "PolicyFolder": "policies" }</c>
/// (relative paths are relative to the modules file). Use the id <c>skills</c>: skills and server instructions refer to <c>skills.find</c>.</summary>
public sealed class SkillsModuleFactory : IHostModuleFactory
{
    public string Type => "skills";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "Business skills",
        Publisher = "OpenTail",
        Description = "Serves the business's published skills (procedures for kinds of situation) and publishes new versions through approval.",
        Notes =
        [
            "Skills are served only from this module's own store, never from outside content. Only an approved publish writes to it.",
            "Skills guide the agent; they never change policy. A tool a skill names is still governed by its own policy entry.",
            "publish is an administration tool: only an agent profile that names it can call it, and its policy must be Queue, Human, a ReviewerGroup, SelfApproval Forbid and AllowEdits false.",
            "The suggested policy needs one approval to move the staging label and two (different people) to move production.",
            "Every skills.get is audited as skill_served with the skill id and version.",
        ],
        SuggestedPolicyJson = $$"""
            { "Tools": {
                "{{id}}.find":    { "Mode": "Execute" },
                "{{id}}.get":     { "Mode": "Execute" },
                "{{id}}.publish": { "Mode": "Queue", "Reviewer": "Human", "ReviewerGroup": "skill-approvers", "SelfApproval": "Forbid",
                                    "AllowEdits": false, "ExpireAfter": "7.00:00:00",
                                    "Rules": [ { "Name": "production-needs-two", "Mode": "Queue", "RequiredApprovals": 2,
                                                 "When": { "not": { "arg": "/label", "equals": "staging" } } } ] }
            } }
            """,
        ServerInstructions = $"Before handling an email, an invoice or a ticket, call {id}.find with the situation, then {id}.get for the skill that matches, and follow it.",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.Store)) throw new ArgumentException("Config.Store is required.");
        if (!SkillStore.Labels.Contains(c.Label)) throw new ArgumentException($"Config.Label must be one of {string.Join(", ", SkillStore.Labels)}.");
        string Full(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(context.BaseDirectory, p));
        var store = Full(c.Store);
        Directory.CreateDirectory(store);
        var policies = c.PolicyFolder is null ? null : Full(c.PolicyFolder);
        if (policies is not null && !Directory.Exists(policies)) throw new ArgumentException($"Config.PolicyFolder {policies} does not exist.");
        IHostModule module = new SkillsModule(new SkillsModuleOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            Store = store,
            Label = c.Label,
            PolicyFolder = policies,
            AllowPublish = c.AllowPublish,
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string Store { get; init; } = "";
        public string Label { get; init; } = "production";
        public string? PolicyFolder { get; init; }
        public bool AllowPublish { get; init; } = true;
    }
}
