using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Aoleg.MiniMaxH3Speed;

/// <summary>SwarmUI extension for the MiniMax-H3 SPEED sampler (progressive-resolution video diffusion).
/// The comfy-side nodes are the MiniMaxH3SPEEDSampler / MiniMaxH3SPEEDSamplerManual nodes from this same repo (auto-installable).
/// This build registers the parameters and the install/mismatch plumbing only; the workflow rewrite is added in later stages.</summary>
public class MiniMaxH3SPEEDExtension : Extension
{
    public const string FeatureId = "minimax_h3_speed";

    /// <summary>Repo the backend node pack is installed from. Must be the same repo this extension ships in: SwarmUI sends node inputs by name and ComfyUI silently drops any the installed node does not declare, so a node pack from another repo (upstream has no C# side) turns every parameter added here into a no-op with no error. SwarmUI clones this into DLNodes/ComfyUI-MiniMax-H3-SPEED, skips the clone if that folder exists, and git-pulls it on every startup.</summary>
    public const string RepoUrl = "https://github.com/aoleg/ComfyUI-MiniMax-H3-SPEED";

    public const string AutomaticNode = "MiniMaxH3SPEEDSampler";

    public const string ManualNode = "MiniMaxH3SPEEDSamplerManual";

    public const string HarvestNode = "MiniMaxH3HarvestToConfig";

    /// <summary>Process-wide key for the <see cref="ClaimInit"/> guard.</summary>
    public const string InitClaimKey = "Aoleg.MiniMaxH3Speed.Initialized";

    /// <summary>The only samplers the node accepts (its `sampler_name` combo). Swarm's main Sampler parameter is validated against this list.</summary>
    public static string[] SupportedSamplers = ["euler", "heun", "dpm_2", "exp_heun_2_x0", "res_multistep"];

    /// <summary>Inputs this extension will send to the Automatic node. Used only to warn about a version mismatch.</summary>
    public static string[] AutomaticInputs = ["noise", "guider", "sigmas", "latent_image", "stages", "noise_policy", "Tolerance (Delta)", "noise_amplitude", "noise_decay_exponent", "seed_offset", "sampler_name"];

    /// <summary>Inputs this extension will send to the Manual node. Used only to warn about a version mismatch.</summary>
    public static string[] ManualInputs = ["noise", "guider", "sigmas", "latent_image", "noise_policy", "seed_offset", "ratio_mode",
        "transition_goal_1", "transition_resolution_1", "transition_goal_2", "transition_resolution_2",
        "transition_goal_3", "transition_resolution_3", "transition_goal_4", "transition_resolution_4", "sampler_name"];

    public static T2IRegisteredParam<string> Mode, NoisePolicy, ManualSchedule, RatioMode;

    public static T2IRegisteredParam<int> Stages;

    public static T2IRegisteredParam<double> Tolerance, NoiseAmplitude, NoiseDecayExponent;

    public static T2IRegisteredParam<long> SeedOffset;

    public static T2IParamGroup H3SpeedGroup;

    /// <summary>Claims the one-per-process init slot. The install button clones this same repo into
    /// BuiltinExtensions/ComfyUIBackend/DLNodes/ComfyUI-MiniMax-H3-SPEED/ to supply the comfy-side nodes, and SwarmUI.csproj's
    /// compile glob only excludes Extensions/**, so that clone's copy of this file also compiles into the core SwarmUI assembly.
    /// Both copies get OnInit() called. A static bool cannot guard that: the two copies are different types in different
    /// assemblies with separate statics. AppDomain data is one slot for the whole process, whichever load context a copy is in.</summary>
    private static bool ClaimInit()
    {
        if (AppDomain.CurrentDomain.GetData(InitClaimKey) is not null)
        {
            return false;
        }
        AppDomain.CurrentDomain.SetData(InitClaimKey, InitClaimKey);
        return true;
    }

    public override void PopulateMetadata()
    {
        base.PopulateMetadata();
        Description = "MiniMax-H3 SPEED: runs the early denoising steps of a MiniMax H3 video generation on a reduced latent grid, then expands to full resolution, for faster video sampling.";
        ExtensionAuthor = "aoleg";
        License = "PolyForm Noncommercial 1.0.0";
        ReadmeURL = RepoUrl;
        Tags = ["parameters", "nodes"];
    }

    public override void OnInit()
    {
        // Dictionary assignments: safe to repeat, so they stay outside the guard and the feature is registered whichever copy runs first.
        InstallableFeatures.RegisterInstallableFeature(new("MiniMax H3 SPEED", FeatureId, RepoUrl, "aoleg", $"This will install the ComfyUI-MiniMax-H3-SPEED node pack from {RepoUrl} (PolyForm Noncommercial 1.0.0, by StanLukuvka).\nThis must be the same repo this extension came from - the parameters it sends need the matching node version.\nDo you wish to install?"));
        ComfyUIBackendExtension.NodeToFeatureMap[AutomaticNode] = FeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap[ManualNode] = FeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap[HarvestNode] = FeatureId;
        if (!ClaimInit())
        {
            Logs.Debug("[H3 SPEED] MiniMaxH3SPEEDExtension.OnInit() ran again (duplicate copy compiled from BuiltinExtensions/ComfyUIBackend/DLNodes/ComfyUI-MiniMax-H3-SPEED/); skipping duplicate param/script registration.");
            return;
        }
        ScriptFiles.Add("assets/h3speed_install.js");
        ComfyUIBackendExtension.RawObjectInfoParsers.Add(WarnOnNodeVersionMismatch);
        H3SpeedGroup = new("MiniMax H3 SPEED", Toggles: true, Open: false, IsAdvanced: true,
            Description: "SPEED for MiniMax H3: runs the early denoising steps on a reduced video latent grid, then expands to full resolution, cutting generation time.\nMiniMax H3 only. Audio always stays at full resolution.\nThe main 'Sampler' parameter is used as the solver and must be Euler, Heun, DPM2, EXP Heun 2 x0 or Res MultiStep.\nNot applied with a mask, an Init Image, a Video Audio Input, or Audio Silent Prefix/Suffix (the node needs empty, unmasked latents).\nCalibrated at 28 to 32 steps; at Swarm's default 20 video steps prefer 2 stages.");
        Mode = T2IParamTypes.Register<string>(new("[H3 SPEED] Mode", "[H3 SPEED]\n'Automatic' picks the resolution transitions from 'Stages', 'Tolerance' and the spectrum fit ('Noise Amplitude' / 'Noise Decay Exponent').\n'Manual' uses the explicit ladder in '[H3 SPEED] Manual Schedule'.",
            "automatic", Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 1, CanSectionalize: true,
            GetValues: (_) => ["automatic///Automatic (stages + tolerance)", "manual///Manual schedule"]
            ));
        Stages = T2IParamTypes.Register<int>(new("[H3 SPEED] Stages", "[H3 SPEED]\nNumber of resolution stages for Automatic mode.\n2 = 0.5 then 1.0. 3 = 0.33, 0.66, 1.0. 4 = 0.25, 0.5, 0.75, 1.0.\nAt 20 video steps the middle stages of a 3- or 4-stage ladder are only one or two steps long, so 2 is the useful default there; 3 and 4 need 28 or more steps.",
            "2", Min: 2, Max: 4, Step: 1, Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 2, CanSectionalize: true
            ));
        Tolerance = T2IParamTypes.Register<double>(new("[H3 SPEED] Tolerance", "[H3 SPEED]\nNoise-dominated tolerance (delta) for Automatic mode. Smaller values transition to full resolution earlier (higher quality, less speedup).\n0.005 is the shipped conservative calibration, 0.01 the balanced one, 0.05 a fast draft with visible artifacts.\nThe shipped 'Noise Amplitude' / 'Noise Decay Exponent' defaults belong to the 0.005 fit; the 0.01 fit uses A 12.436 / beta 0.786.",
            "0.005", Min: 0.0001, Max: 0.5, Step: 0.001, Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 3, CanSectionalize: true,
            Examples: ["0.005", "0.01", "0.05"]
            ));
        NoiseAmplitude = T2IParamTypes.Register<double>(new("[H3 SPEED] Noise Amplitude", "[H3 SPEED]\nPower-law amplitude 'A' of the H3 residual spectrum fit (P = A * omega^-beta), from the node's Sigma Harvest calibration.\nThe default is the shipped Euler-derived 0.005 fit. Re-harvest in ComfyUI when you change the checkpoint, sampler, LoRA or step count.",
            "12.105", Min: 0.0001, Max: 1000000, Step: 0.0001, Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 4, CanSectionalize: true
            ));
        NoiseDecayExponent = T2IParamTypes.Register<double>(new("[H3 SPEED] Noise Decay Exponent", "[H3 SPEED]\nPower-law decay exponent 'beta' of the H3 residual spectrum fit, from the node's Sigma Harvest calibration.\nThe default is the shipped Euler-derived 0.005 fit.",
            "0.773", Min: 0.0001, Max: 10, Step: 0.0001, Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 5, CanSectionalize: true
            ));
        NoisePolicy = T2IParamTypes.Register<string>(new("[H3 SPEED] Noise Policy", "[H3 SPEED]\nHow newly exposed frequency bands are filled at each resolution transition.\n'direct_coarse' (default) uses deterministic transition-seeded Gaussian noise.\n'coupled_full_grid' derives them from one seeded full-resolution field. The node's author has not confirmed it improves quality.",
            "direct_coarse", Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 6, CanSectionalize: true,
            GetValues: (_) => ["direct_coarse///Direct coarse (default)", "coupled_full_grid///Coupled full grid"]
            ));
        SeedOffset = T2IParamTypes.Register<long>(new("[H3 SPEED] Seed Offset", "[H3 SPEED]\nOffset added to the generation seed for the high-frequency fill at each transition ('direct_coarse' only).\nLeave at 10000 unless you want a different fill pattern for the same seed.",
            "10000", Min: 0, Max: int.MaxValue, Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 7, CanSectionalize: true
            ));
        ManualSchedule = T2IParamTypes.Register<string>(new("[H3 SPEED] Manual Schedule", "[H3 SPEED]\nManual mode only. Up to four 'goal:resolution' pairs, comma-separated, for example '3:0.25,5:0.5,8:0.75,15:1.0'.\nFor every stage except the last, 'goal' is where that stage ends (a step index, or a 0-1 fraction when 'Ratio Mode' is 'ratio') and 'resolution' is its scale. The last stage always runs to the end and must be 1.0.\nResolutions must increase. Fewer than four pairs disables the remaining stages.",
            "3:0.25,5:0.5,8:0.75,15:1.0", Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 8, CanSectionalize: true, DependNonDefault: Mode.Type.ID,
            Examples: ["7:0.5,20:1.0", "3:0.25,5:0.5,8:0.75,15:1.0"]
            ));
        RatioMode = T2IParamTypes.Register<string>(new("[H3 SPEED] Ratio Mode", "[H3 SPEED]\nManual mode only. 'steps' reads each goal as a global step index; 'ratio' reads it as a 0-1 fraction of the schedule.",
            "steps", Group: H3SpeedGroup, FeatureFlag: FeatureId, OrderPriority: 9, CanSectionalize: true, DependNonDefault: Mode.Type.ID,
            GetValues: (_) => ["steps///Step index", "ratio///Fraction of schedule"]
            ));
        // Runs just after the main sampler step (-5), before the refiner (-4): rewrites the main sampler node in place so all
        // downstream connections to it remain valid. The video-model (image-to-video) path is a separate hook, added in stage 4.
        WorkflowGenerator.AddStep(ApplyTextToVideo, -4.5);
    }

    /// <summary>Warns when the installed node pack does not match this extension.
    /// ComfyUI builds a node's arguments from its own INPUT_TYPES and ignores anything else in the prompt, so an input the installed node
    /// does not declare is silently dropped: the control appears in the UI, changes nothing, and reports no error.</summary>
    public static void WarnOnNodeVersionMismatch(JObject rawObjectInfo)
    {
        CheckNode(rawObjectInfo, AutomaticNode, AutomaticInputs);
        CheckNode(rawObjectInfo, ManualNode, ManualInputs);
    }

    private static void CheckNode(JObject rawObjectInfo, string nodeName, string[] requiredInputs)
    {
        if (rawObjectInfo[nodeName] is not JObject node)
        {
            return; // Not installed at all; the feature flag already covers that case.
        }
        HashSet<string> declared = [];
        foreach (string section in new[] { "required", "optional" })
        {
            if (node["input"]?[section] is JObject inputs)
            {
                foreach (JProperty prop in inputs.Properties())
                {
                    declared.Add(prop.Name);
                }
            }
        }
        string[] missingInputs = [.. requiredInputs.Where(i => !declared.Contains(i))];
        if (missingInputs.Any())
        {
            Logs.Warning($"[H3 SPEED] The installed ComfyUI-MiniMax-H3-SPEED node pack does not match this extension: node '{nodeName}' does not accept {string.Join(", ", missingInputs)}. "
                + "ComfyUI ignores inputs a node does not declare, so those options will do nothing (with no error). "
                + $"Update the node pack - its git remote must be {RepoUrl}.");
        }
        // The sampler list lives in the node; a pack that lacks one of these fails loudly at generation time, but naming it here is more actionable.
        if (ComfyUIBackendExtension.TryGetRequiredInputs(rawObjectInfo, nodeName, "sampler_name", out JToken samplers))
        {
            HashSet<string> available = [.. samplers.Select(s => $"{s}")];
            string[] missingSamplers = [.. SupportedSamplers.Where(s => !available.Contains(s))];
            if (missingSamplers.Any())
            {
                Logs.Warning($"[H3 SPEED] The installed node pack's '{nodeName}' does not offer these samplers: {string.Join(", ", missingSamplers)}. "
                    + "Selecting one will fail the generation. Update the node pack.");
            }
        }
    }

    /// <summary>Scheduler names that comfy's stock BasicScheduler node accepts. Swarm-custom schedulers (align_your_steps, flux2, ltxv, ...) are computed inside SwarmKSampler and cannot be rebuilt from stock nodes.</summary>
    public static HashSet<string> BasicSchedulers = ["normal", "karras", "exponential", "sgm_uniform", "simple", "ddim_uniform", "beta", "linear_quadratic", "kl_optimal"];

    /// <summary>Text-to-video path: H3 as the main model, sampled by the core's node "10". Rewrites that node in place into the H3 SPEED node.</summary>
    public static void ApplyTextToVideo(WorkflowGenerator g)
    {
        // Any one group-param being present indicates the group toggle is enabled.
        if (!g.UserInput.TryGet(Mode, out string mode))
        {
            return;
        }
        if (!g.Features.Contains(FeatureId))
        {
            throw new SwarmUserErrorException("MiniMax H3 SPEED parameters specified, but the ComfyUI-MiniMax-H3-SPEED node pack isn't installed on the backend.");
        }
        if (!g.IsMiniMaxH3())
        {
            Logs.Warning("[H3 SPEED] the main model is not MiniMax H3; SPEED skipped.");
            return;
        }
        // "10" is the fixed ID the core generator gives the main (base stage) sampler node.
        if (g.Workflow["10"] is not JObject samplerNode || samplerNode["inputs"] is not JObject samplerInputs)
        {
            Logs.Warning("[H3 SPEED] no main sampler node found in the workflow; SPEED skipped.");
            return;
        }
        string reason = WhyNotApplicable(g, samplerInputs);
        if (reason is not null)
        {
            Logs.Warning($"[H3 SPEED] {reason}; SPEED skipped.");
            return;
        }
        RewriteSampler(g, samplerNode, mode, T2IParamInput.SectionID_BaseOnly, "main sampler");
    }

    /// <summary>Returns null when the sampler node can be rewritten, else a one-line reason. The node needs empty, unmasked video and audio latent streams straight from the empty-latent node, and one of five samplers.</summary>
    public static string WhyNotApplicable(WorkflowGenerator g, JObject samplerInputs)
    {
        if (g.FinalMask is not null)
        {
            return "a mask is in use (the node needs unmasked latents)";
        }
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image _))
        {
            return "an Init Image is in use (the node needs empty latent streams)";
        }
        if ((g.UserInput.TryGet(T2IParamTypes.AudioSilentPrefixDuration, out double prefix) && prefix > 0)
            || (g.UserInput.TryGet(T2IParamTypes.AudioSilentSuffixDuration, out double suffix) && suffix > 0))
        {
            return "Audio Silent Prefix/Suffix is set (it writes silence into the audio latent and masks it; the node needs empty, unmasked streams)";
        }
        // Backstop for any other latent preparation: the node rejects non-empty or masked streams, so only the raw empty AV latent qualifies.
        if (samplerInputs["latent_image"] is not JArray latentRef || g.Workflow[$"{latentRef[0]}"] is not JObject latentNode)
        {
            return "the sampler's latent input is not a node reference";
        }
        string latentClass = $"{latentNode["class_type"]}";
        if (latentClass != "SwarmEmptyMiniMaxH3LatentAV")
        {
            return $"the latent comes from a '{latentClass}' node rather than straight from the empty MiniMax H3 latent (the node needs empty, unmasked streams)";
        }
        if (samplerInputs["sampler_name"] is JToken samplerTok && !SupportedSamplers.Contains($"{samplerTok}"))
        {
            return $"sampler '{samplerTok}' is not supported by the node (use {string.Join(", ", SupportedSamplers)})";
        }
        return null;
    }

    /// <summary>Rewrites a SwarmKSampler / KSamplerAdvanced node in place into the H3 SPEED node (Automatic or Manual), mirroring the core's own custom-sampling emission for the noise, guider and sigmas.</summary>
    public static void RewriteSampler(WorkflowGenerator g, JObject samplerNode, string mode, int sectionId, string label)
    {
        JObject samplerInputs = (JObject)samplerNode["inputs"];
        string classType = $"{samplerNode["class_type"]}";
        if (classType != "SwarmKSampler" && classType != "KSamplerAdvanced")
        {
            Logs.Warning($"[H3 SPEED] {label} is a '{classType}', which SPEED doesn't know how to rebuild; SPEED skipped.");
            return;
        }
        JToken model = samplerInputs["model"], pos = samplerInputs["positive"], neg = samplerInputs["negative"], latent = samplerInputs["latent_image"];
        long seed = (long)samplerInputs["noise_seed"];
        int steps = (int)samplerInputs["steps"];
        double cfg = (double)samplerInputs["cfg"];
        string samplerName = $"{samplerInputs["sampler_name"]}";
        string scheduler = $"{samplerInputs["scheduler"]}".ToLowerFast();
        int startStep = (int)samplerInputs["start_at_step"];
        int endStep = (int)samplerInputs["end_at_step"];
        bool addNoise = $"{samplerInputs["add_noise"]}" == "enable";
        double sigmin = samplerInputs.TryGetValue("sigma_min", out JToken sigminTok) ? (double)sigminTok : g.UserInput.Get(T2IParamTypes.SamplerSigmaMin, -1);
        double sigmax = samplerInputs.TryGetValue("sigma_max", out JToken sigmaxTok) ? (double)sigmaxTok : g.UserInput.Get(T2IParamTypes.SamplerSigmaMax, -1);
        double rho = samplerInputs.TryGetValue("rho", out JToken rhoTok) ? (double)rhoTok : g.UserInput.Get(T2IParamTypes.SamplerRho, 7);
        if (samplerInputs.TryGetValue("var_seed_strength", out JToken varStrength) && (double)varStrength > 0)
        {
            Logs.Warning("[H3 SPEED] Variation Seed is not supported together with SPEED and will be ignored.");
        }
        string noiseNode = addNoise
            ? g.CreateNode("RandomNoise", new JObject()
            {
                ["noise_seed"] = seed
            })
            : g.CreateNode("DisableNoise", new JObject());
        string guiderNode = g.CreateNode("CFGGuider", new JObject()
        {
            ["model"] = model,
            ["positive"] = pos,
            ["negative"] = neg,
            ["cfg"] = cfg
        });
        JArray sigmas;
        if (scheduler == "turbo")
        {
            string turboNode = g.CreateNode("SDTurboScheduler", new JObject()
            {
                ["model"] = model,
                ["steps"] = steps,
                ["denoise"] = 1
            });
            sigmas = [turboNode, 0];
        }
        else if (scheduler == "karras")
        {
            string karrasNode = g.CreateNode("KarrasScheduler", new JObject()
            {
                ["steps"] = steps,
                ["sigma_max"] = sigmax <= 0 ? 14.614642 : sigmax,
                ["sigma_min"] = sigmin <= 0 ? 0.0291675 : sigmin,
                ["rho"] = rho
            });
            sigmas = [karrasNode, 0];
        }
        else
        {
            if (!BasicSchedulers.Contains(scheduler))
            {
                Logs.Warning($"[H3 SPEED] scheduler '{scheduler}' is Swarm-custom and can't be rebuilt with stock comfy nodes; using 'simple' instead.");
                scheduler = "simple";
            }
            string basicNode = g.CreateNode("BasicScheduler", new JObject()
            {
                ["model"] = model,
                ["steps"] = steps,
                ["scheduler"] = scheduler,
                ["denoise"] = 1
            });
            sigmas = [basicNode, 0];
        }
        if (startStep > 0)
        {
            string afterStart = g.CreateNode("SplitSigmas", new JObject()
            {
                ["sigmas"] = sigmas,
                ["step"] = startStep
            });
            sigmas = [afterStart, 1];
        }
        if (endStep < steps)
        {
            string beforeEnd = g.CreateNode("SplitSigmas", new JObject()
            {
                ["sigmas"] = sigmas,
                ["step"] = endStep
            });
            sigmas = [beforeEnd, 0];
        }
        JObject inputs = new()
        {
            ["noise"] = new JArray() { noiseNode, 0 },
            ["guider"] = new JArray() { guiderNode, 0 },
            ["sigmas"] = sigmas,
            ["latent_image"] = latent,
            ["noise_policy"] = g.UserInput.Get(NoisePolicy, "direct_coarse", sectionId: sectionId),
            ["seed_offset"] = g.UserInput.Get(SeedOffset, 10000, sectionId: sectionId),
            ["sampler_name"] = samplerName
        };
        if (mode == "manual")
        {
            (double goal, double resolution)[] ladder = ParseManualSchedule(g.UserInput.Get(ManualSchedule, "", sectionId: sectionId));
            inputs["ratio_mode"] = g.UserInput.Get(RatioMode, "steps", sectionId: sectionId);
            for (int i = 0; i < 4; i++)
            {
                inputs[$"transition_goal_{i + 1}"] = ladder[i].goal;
                inputs[$"transition_resolution_{i + 1}"] = ladder[i].resolution;
            }
            samplerNode["class_type"] = ManualNode;
        }
        else
        {
            inputs["stages"] = g.UserInput.Get(Stages, 2, sectionId: sectionId);
            inputs["Tolerance (Delta)"] = g.UserInput.Get(Tolerance, 0.005, sectionId: sectionId);
            inputs["noise_amplitude"] = g.UserInput.Get(NoiseAmplitude, 12.105, sectionId: sectionId);
            inputs["noise_decay_exponent"] = g.UserInput.Get(NoiseDecayExponent, 0.773, sectionId: sectionId);
            samplerNode["class_type"] = AutomaticNode;
        }
        samplerNode["inputs"] = inputs;
        Logs.Debug($"[H3 SPEED] {label} rebuilt as {samplerNode["class_type"]} ({mode}, sampler {samplerName}, scheduler {scheduler}, {steps} steps).");
    }

    /// <summary>Parses "goal:resolution,goal:resolution,..." (one to four pairs) into exactly four pairs; unused pairs are 0:0, which the node treats as disabled stages.</summary>
    public static (double goal, double resolution)[] ParseManualSchedule(string text)
    {
        (double goal, double resolution)[] ladder = new (double, double)[4];
        string[] pairs = [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (pairs.Length == 0 || pairs.Length > 4)
        {
            throw new SwarmUserErrorException($"[H3 SPEED] Manual Schedule must have one to four 'goal:resolution' pairs, for example '7:0.5,20:1.0'; got '{text}'.");
        }
        for (int i = 0; i < pairs.Length; i++)
        {
            string[] parts = pairs[i].Split(':');
            if (parts.Length != 2 || !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double goal)
                || !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double resolution))
            {
                throw new SwarmUserErrorException($"[H3 SPEED] Manual Schedule pair '{pairs[i]}' is not 'goal:resolution' with numbers (use a dot as the decimal separator).");
            }
            ladder[i] = (goal, resolution);
        }
        return ladder;
    }
}
