using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B1/B2) — Walks a parsed <c>.scene</c> Echo tree. B1 read path turns it into
/// <see cref="SceneHierarchy"/>; B2 write path uses the tree navigation/set helpers to mutate the
/// scene in place, then write it back. Engine-agnostic: no <c>Scene</c> deserialization.
///
/// <para>Tree shape is known from the editor's save path: root → <c>serializeObj.array[]</c>
/// (GameObjects); each GO has <c>Name</c>/<c>Identifier</c>/<c>TagIndex</c>/<c>LayerIndex</c>/
/// <c>Transform</c>/<c>Components[]</c>/<c>Children</c>.</para>
/// </summary>
internal static class EchoFacade
{
    /// <summary>The <c>serializeObj.array</c> GameObject list on a parsed scene root (may be empty).</summary>
    public static EchoObject GetGameObjectArray(EchoObject sceneRoot)
    {
        if (sceneRoot.TryGet("serializeObj", out EchoObject? ser)
            && ser!.TryGet("array", out EchoObject? arr))
        {
            return arr!;
        }

        return EchoObject.NewList();
    }

    /// <summary>Find a GameObject node in the scene by its Identifier, or null. ★ Gen-P0.2 — the
    /// editor's save format nests child GameObjects inline under their parent's Children array and
    /// leaves a {$id,$type} reference stub in the flat serializeObj.array. Resolve stubs and recurse
    /// into Children so nested GameObjects are addressable by every write tool.</summary>
    public static EchoObject? FindGameObject(EchoObject sceneRoot, string identifier)
    {
        foreach (EchoObject go in EnumerateGameObjects(sceneRoot))
        {
            if (TryGetStr(go, "Identifier") == identifier)
            {
                return go;
            }
        }

        return null;
    }

    /// <summary>★ Gen-P0.2 — Every full GameObject definition in the scene (flat roots + all inline
    /// nested children), depth-first in flat-array order. Reference stubs are skipped (their full
    /// definition is emitted when its parent's Children are walked).</summary>
    public static IEnumerable<EchoObject> EnumerateGameObjects(EchoObject sceneRoot)
    {
        foreach (EchoObject entry in GetGameObjectArray(sceneRoot).List)
        {
            if (!IsFullGameObjectNode(entry)) continue;
            foreach (EchoObject node in EnumerateSubtree(entry))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<EchoObject> EnumerateSubtree(EchoObject go)
    {
        yield return go;
        if (go.TryGet("Children", out EchoObject? kids) && kids!.TagType == EchoType.List)
        {
            foreach (EchoObject child in kids.List)
            {
                if (!IsFullGameObjectNode(child)) continue;
                foreach (EchoObject node in EnumerateSubtree(child))
                {
                    yield return node;
                }
            }
        }
    }

    /// <summary>A full GameObject definition carries an Identifier; a reference stub is only {$id,$type}.</summary>
    public static bool IsFullGameObjectNode(EchoObject node)
        => node.TagType == EchoType.Compound && node.TryGet("Identifier", out EchoObject? id)
           && id!.TagType == EchoType.String;

    /// <summary>★ Gen-P0.2 — Map every full GameObject definition's $id to its node, so flat-array
    /// reference stubs can be resolved back to the inline definition under a parent's Children.</summary>
    public static Dictionary<int, EchoObject> BuildEchoIdMap(EchoObject sceneRoot)
    {
        var map = new Dictionary<int, EchoObject>();
        foreach (EchoObject node in EnumerateGameObjects(sceneRoot))
        {
            if (node.TryGet("$id", out EchoObject? id) && id!.TagType == EchoType.Int)
            {
                map[id.IntValue] = node;
            }
        }
        return map;
    }

    /// <summary>Resolve a flat serializeObj.array entry to its full GameObject definition: the entry
    /// itself when it carries a body, else the inline definition its {$id} stub points to. Null when
    /// the stub is dangling.</summary>
    public static EchoObject? ResolveFlatEntry(Dictionary<int, EchoObject> echoIdMap, EchoObject entry)
    {
        if (IsFullGameObjectNode(entry)) return entry;
        if (entry.TagType == EchoType.Compound
            && entry.TryGet("$id", out EchoObject? id) && id!.TagType == EchoType.Int
            && echoIdMap.TryGetValue(id.IntValue, out EchoObject? node))
        {
            return node;
        }
        return null;
    }

    /// <summary>Largest $id used anywhere in the tree (0 when none). New inline definitions must mint
    /// ids above this so reference stubs stay unambiguous.</summary>
    public static int MaxEchoId(EchoObject node)
    {
        int max = 0;
        if (node.TagType == EchoType.Compound)
        {
            if (node.TryGet("$id", out EchoObject? id) && id!.TagType == EchoType.Int && id.IntValue > max)
            {
                max = id.IntValue;
            }
            foreach (string key in node.GetNames())
            {
                if (key == "$id") continue;
                if (node.TryGet(key, out EchoObject? child) && child is not null)
                {
                    int childMax = MaxEchoId(child);
                    if (childMax > max) max = childMax;
                }
            }
        }
        else if (node.TagType == EchoType.List)
        {
            foreach (EchoObject child in node.List)
            {
                int childMax = MaxEchoId(child);
                if (childMax > max) max = childMax;
            }
        }
        return max;
    }

    /// <summary>★ P0-4 — The Components list on a GameObject node. Creates it if missing (a GO
    /// serialized without a Components array is valid; we add an empty one so component_add works).</summary>
    public static EchoObject GetComponentsList(EchoObject go)
    {
        if (!go.TryGet("Components", out EchoObject? comps) || comps!.TagType != EchoType.List)
        {
            EchoObject made = EchoObject.NewList();
            go["Components"] = made;
            return made;
        }
        return comps!;
    }

    /// <summary>★ Gen-P0.2 — The Children list on a GameObject node. Creates it if missing.</summary>
    public static EchoObject GetChildrenList(EchoObject go)
    {
        if (!go.TryGet("Children", out EchoObject? kids) || kids!.TagType != EchoType.List)
        {
            EchoObject made = EchoObject.NewList();
            go["Children"] = made;
            return made;
        }
        return kids!;
    }

    /// <summary>★ Gen-P0.2 — Echo's compact Guid form: {"$t":"g","$v":"..."} (what the serializer
    /// emits for Guid-typed fields like MonoBehaviour._identifier and the _goIdentifiers entries).</summary>
    public static EchoObject MakeGuidTag(string guid)
    {
        EchoObject tag = EchoObject.NewCompound();
        tag["$t"] = new EchoObject("g");
        tag["$v"] = new EchoObject(guid);
        return tag;
    }

    /// <summary>Read a component's _identifier, accepting both the editor's compact Guid form
    /// ({$t:"g",$v:"..."}) and the legacy plain-string form older offline writes produced.</summary>
    public static string? ReadComponentIdentifier(EchoObject comp)
    {
        if (!comp.TryGet("_identifier", out EchoObject? id) || id is null) return null;
        if (id.TagType == EchoType.String) return id.StringValue;
        if (id.TagType == EchoType.Compound
            && id.TryGet("$v", out EchoObject? v) && v!.TagType == EchoType.String)
        {
            return v.StringValue;
        }
        return null;
    }

    private const string GuidArrayType =
        "System.Guid[], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";
    private const string IntArrayType =
        "System.Int32[], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";

    /// <summary>
    /// ★ Gen-P0.2 — Recompute the scene's parallel identity arrays (_goIdentifiers, _compIdentifiers,
    /// _compIdOffsets) from the GameObject tree, mirroring Scene.OnBeforeSerialize. This is the load-
    /// bearing step of every structural offline edit: GameObject.Deserialize ALWAYS mints fresh Guids
    /// and Scene.OnAfterDeserialize restores stable identities from these arrays — and it skips the
    /// restore entirely when _goIdentifiers.Length != serializeObj.Length. An offline create/delete/
    /// component-add that doesn't maintain the arrays therefore destroys every stable identity in the
    /// scene at its next load.
    /// </summary>
    public static void RebuildIdentifierArrays(EchoObject sceneRoot)
    {
        if (!sceneRoot.TryGet("serializeObj", out EchoObject? ser) || ser!.TagType != EchoType.Compound
            || !ser.TryGet("array", out EchoObject? arr) || arr!.TagType != EchoType.List)
        {
            return; // not a scene tree (e.g. a prefab root) — nothing to maintain
        }

        Dictionary<int, EchoObject> idMap = BuildEchoIdMap(sceneRoot);
        var goIds = new List<string>();
        var compIds = new List<string>();
        var offsets = new List<int>();

        foreach (EchoObject entry in arr.List)
        {
            EchoObject? node = ResolveFlatEntry(idMap, entry);
            offsets.Add(compIds.Count);
            goIds.Add(node is not null
                ? TryGetStr(node, "Identifier") ?? Guid.Empty.ToString()
                : Guid.Empty.ToString());
            if (node is not null
                && node.TryGet("Components", out EchoObject? comps) && comps!.TagType == EchoType.List)
            {
                foreach (EchoObject comp in comps.List)
                {
                    compIds.Add(ReadComponentIdentifier(comp) ?? Guid.Empty.ToString());
                }
            }
        }
        offsets.Add(compIds.Count);

        WriteGuidArrayField(sceneRoot, "_goIdentifiers", goIds);
        WriteGuidArrayField(sceneRoot, "_compIdentifiers", compIds);
        WriteIntArrayField(sceneRoot, "_compIdOffsets", offsets);
    }

    private static void WriteGuidArrayField(EchoObject root, string key, List<string> guids)
    {
        EchoObject wrapper = GetOrCreateArrayWrapper(root, key, GuidArrayType);
        EchoObject list = EchoObject.NewList();
        foreach (string g in guids)
        {
            list.ListAdd(MakeGuidTag(g));
        }
        wrapper["array"] = list;
    }

    private static void WriteIntArrayField(EchoObject root, string key, List<int> values)
    {
        EchoObject wrapper = GetOrCreateArrayWrapper(root, key, IntArrayType);
        EchoObject list = EchoObject.NewList();
        foreach (int v in values)
        {
            list.ListAdd(new EchoObject(v));
        }
        wrapper["array"] = list;
    }

    private static EchoObject GetOrCreateArrayWrapper(EchoObject root, string key, string typeName)
    {
        // Preserve an existing wrapper (keeps its $id/$type stable → minimal diffs); only mint one
        // when the scene never had the arrays (e.g. hand-written or legacy scenes).
        if (root.TryGet(key, out EchoObject? existing) && existing!.TagType == EchoType.Compound)
        {
            return existing;
        }
        EchoObject wrapper = EchoObject.NewCompound();
        wrapper["$type"] = new EchoObject(typeName);
        root[key] = wrapper;
        return wrapper;
    }

    /// <summary>
    /// ★ Gen-P0.2 — Remove a GameObject (and its whole inline subtree) from the scene: drops the flat
    /// array's full definition or reference stub for every node of the subtree, and unlinks the inline
    /// definition from its parent's Children when nested. Returns false when the identifier is unknown.
    /// Callers must <see cref="RebuildIdentifierArrays"/> afterwards.
    /// </summary>
    public static bool RemoveGameObjectSubtree(EchoObject sceneRoot, string identifier)
    {
        EchoObject? target = FindGameObject(sceneRoot, identifier);
        if (target is null) return false;

        var doomedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var doomedEchoIds = new HashSet<int>();
        foreach (EchoObject node in EnumerateSubtree(target))
        {
            string? id = TryGetStr(node, "Identifier");
            if (id is not null) doomedIdentifiers.Add(id);
            if (node.TryGet("$id", out EchoObject? echoId) && echoId!.TagType == EchoType.Int)
            {
                doomedEchoIds.Add(echoId.IntValue);
            }
        }

        // Flat array: remove full definitions AND stubs belonging to the subtree.
        EchoObject flat = GetGameObjectArray(sceneRoot);
        RemoveMatchingEntries(flat, doomedIdentifiers, doomedEchoIds);

        // Children lists: unlink the subtree root (and any stray stubs) from every remaining GO.
        foreach (EchoObject node in EnumerateGameObjects(sceneRoot))
        {
            if (node.TryGet("Children", out EchoObject? kids) && kids!.TagType == EchoType.List)
            {
                RemoveMatchingEntries(kids, doomedIdentifiers, doomedEchoIds);
            }
        }
        return true;
    }

    private static void RemoveMatchingEntries(
        EchoObject list, HashSet<string> identifiers, HashSet<int> echoIds)
    {
        for (int i = list.List.Count - 1; i >= 0; i--)
        {
            EchoObject entry = list.List[i];
            string? id = TryGetStr(entry, "Identifier");
            bool matchesIdentifier = id is not null && identifiers.Contains(id);
            bool matchesEchoId = entry.TagType == EchoType.Compound
                && entry.TryGet("$id", out EchoObject? echoId) && echoId!.TagType == EchoType.Int
                && echoIds.Contains(echoId.IntValue);
            if (matchesIdentifier || matchesEchoId)
            {
                list.ListRemoveAt(i);
            }
        }
    }

    /// <summary>★ prefab — A .prefab's root IS a single GameObject (with $type envelope). Return it
    /// directly (it has Name/Identifier/Transform/Components/Children like any GO).</summary>
    public static EchoObject GetPrefabRootGameObject(EchoObject prefabRoot) => prefabRoot;

    /// <summary>★ prefab — Recursively find a GameObject by Identifier anywhere in a GO tree (the prefab
    /// root or any descendant via Children). Scene's flat-array search doesn't reach nested children.</summary>
    public static EchoObject? FindGameObjectRecursive(EchoObject node, string identifier)
    {
        if (TryGetStr(node, "Identifier") == identifier) return node;
        if (node.TryGet("Children", out EchoObject? kids) && kids!.TagType == EchoType.List)
        {
            foreach (EchoObject child in kids.List)
            {
                EchoObject? found = FindGameObjectRecursive(child, identifier);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Navigate a slash-separated path (e.g. "Transform/_localPosition/Y") from a node and set the
    /// leaf value. Creates intermediate compounds as needed. Value is inferred: number/bool/string.
    /// ★ Gen-P0.3 — list intermediates are now creatable too: a missing segment whose NEXT segment is
    /// numeric becomes a List, and index == Count appends a new element. That lets one
    /// scene_component_add call seed list-shaped fields on a sparse node, e.g. a MeshRenderer's
    /// "Materials/$values/0/AssetID" (Echo deserializes {$values:[…]} into List&lt;T&gt; using the
    /// field's declared type, so no $type stamp is needed). Index gaps beyond Count are still rejected.
    /// </summary>
    public static void SetPathValue(EchoObject root, string path, string literalValue)
    {
        string[] segments = path.Split('/');
        EchoObject current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string seg = segments[i];
            // ★ List-index navigation: when current is a List and the segment is a numeric index,
            // descend into the element by position; index == Count appends a fresh element (compound
            // or list, decided by the next segment).
            if (current.TagType == EchoType.List)
            {
                if (!int.TryParse(seg, out int idx))
                {
                    throw new InvalidOperationException(
                        $"Cannot navigate list by non-numeric segment '{seg}'. Use a numeric index " +
                        "(e.g. 'Components/0/FieldOfView'). To target a component without knowing its " +
                        "index, use scene_component_patch with a componentIdentifier or componentType.");
                }
                if (idx == current.List.Count)
                {
                    EchoObject appended = IsIndexSegment(segments, i + 1)
                        ? EchoObject.NewList() : EchoObject.NewCompound();
                    current.ListAdd(appended);
                    current = appended;
                    continue;
                }
                if (idx < 0 || idx > current.List.Count)
                {
                    throw new IndexOutOfRangeException(
                        $"List index {idx} is out of range (the list has {current.List.Count} element(s); " +
                        $"index {current.List.Count} would append). " +
                        "Call scene_hierarchy to list the current components and their order.");
                }
                current = current.List[idx];
                continue;
            }

            if (!current.TryGet(seg, out EchoObject? next))
            {
                // Create the missing intermediate: a List when the next segment is a numeric index,
                // else a compound.
                EchoObject made = IsIndexSegment(segments, i + 1)
                    ? EchoObject.NewList() : EchoObject.NewCompound();
                current[seg] = made;
                current = made;
            }
            else
            {
                current = next!;
            }
        }

        // Write the leaf. Same list-vs-compound handling: if current is a list, the leaf must be a
        // numeric index; index == Count appends.
        string leaf = segments[^1];
        if (current.TagType == EchoType.List)
        {
            if (!int.TryParse(leaf, out int leafIdx))
            {
                throw new InvalidOperationException(
                    $"Cannot set list element by non-numeric segment '{leaf}' on a list.");
            }
            if (leafIdx == current.List.Count)
            {
                current.ListAdd(ParseLiteral(literalValue));
                return;
            }
            if (leafIdx < 0 || leafIdx > current.List.Count)
            {
                throw new IndexOutOfRangeException(
                    $"List index {leafIdx} is out of range (the list has {current.List.Count} element(s); " +
                    $"index {current.List.Count} would append).");
            }
            current.List[leafIdx] = ParseLiteral(literalValue, current.List[leafIdx]);
        }
        else
        {
            current.TryGet(leaf, out EchoObject? existingLeaf);
            current[leaf] = ParseLiteral(literalValue, existingLeaf);
        }
    }

    private static bool IsIndexSegment(string[] segments, int index)
        => index < segments.Length && int.TryParse(segments[index], out _);

    /// <summary>Infer an Echo value from a literal string (number/bool/string).</summary>
    /// <param name="existing">
    /// Current value at the target leaf, when present. A float/double leaf keeps its numeric kind
    /// so an integer literal like "5" lands as 5F — a Long would be silently deserialized as 0 by
    /// float-typed fields (e.g. Float3 transform channels).
    /// </param>
    private static EchoObject ParseLiteral(string s, EchoObject? existing = null)
    {
        bool floatTarget = existing != null
            && (existing.TagType == EchoType.Float || existing.TagType == EchoType.Double);
        if (bool.TryParse(s, out bool b)) return new EchoObject(b);
        if (!floatTarget && long.TryParse(s, out long l)) return new EchoObject(l);
        if (double.TryParse(s, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            // Prefer float for typical transform/color fields; Echo stores the inferred type.
            return new EchoObject((float)d);
        }

        return new EchoObject(s);
    }

    public static SceneHierarchy ReadSceneHierarchy(string sceneText, string absolutePath, int maxDepth, int maxObjects = 500, TagLayerResolver? tagLayer = null)
    {
        EchoObject root = EchoObject.ReadFromString(sceneText);

        string sceneName = Path.GetFileNameWithoutExtension(absolutePath);

        // ★ Gen-P0.2 — the editor's save format nests child GameObjects inline under the parent's
        // Children array and leaves {$id,$type} reference stubs in the flat serializeObj.array. The
        // old read treated flat entries as the node list, so stubs surfaced as phantom empty roots and
        // nested children were invisible. Resolve every GO body (wherever it lives), then emit roots
        // with their subtree nested up to maxDepth.
        Dictionary<int, EchoObject> idMap = BuildEchoIdMap(root);

        var allNodes = new List<EchoObject>();
        foreach (EchoObject go in EnumerateGameObjects(root))
        {
            allNodes.Add(go);
        }

        // Child identifiers, resolving stub entries through the $id map so reference-form Children
        // count too (they're how the editor links an already-serialized GO).
        var childIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EchoObject go in allNodes)
        {
            foreach (string cid in ResolveChildIdentifiers(go, idMap))
            {
                childIds.Add(cid);
            }
        }

        var roots = new List<SceneObjectNode>();
        foreach (EchoObject go in allNodes)
        {
            string? id = TryGetStr(go, "Identifier");
            if (id is null || childIds.Contains(id)) continue;
            roots.Add(ReadGameObject(go, tagLayer, idMap, maxDepth));
        }

        int totalObjects = allNodes.Count;

        // ★ P0-3: Response size guard. A pathological scene with thousands of root GameObjects would
        // produce a multi-MB response that blows the MCP 4MB budget. Truncate RootObjects to
        // maxObjects and signal via HasMore; TotalObjects still reports the true count so the AI knows
        // how much was omitted and can narrow its query (e.g. filter by parent Identifier).
        int cap = Math.Clamp(maxObjects, 1, ProtocolConstants.Limits.MaxObjectsPerHierarchy);
        bool hasMore = roots.Count > cap;
        if (hasMore)
        {
            roots = roots.Take(cap).ToList();
        }

        return new SceneHierarchy(absolutePath, sceneName, roots, totalObjects,
            ProvenancedDelta.ContentHash(root), cap, hasMore);
    }

    /// <summary>Identifiers of a GO's children, resolving both inline bodies and {$id} stubs.</summary>
    private static List<string> ResolveChildIdentifiers(EchoObject go, Dictionary<int, EchoObject> idMap)
    {
        var ids = new List<string>();
        if (go.TryGet("Children", out EchoObject? children) && children!.TagType == EchoType.List)
        {
            foreach (EchoObject child in children.List)
            {
                EchoObject? resolved = ResolveFlatEntry(idMap, child);
                string? cid = resolved is not null ? TryGetStr(resolved, "Identifier") : null;
                if (cid is not null) ids.Add(cid);
            }
        }
        return ids;
    }

    /// <summary>★ P0-6 — Read a .mat file's shader reference + properties as a structured
    /// <see cref="MaterialDetail"/>. The .mat root is a single Material node with _shader.AssetID
    /// and _properties.{_colors,_floats,_ints,_vectors2/3/4,_textures,...} buckets.</summary>
    public static MaterialDetail ReadMaterial(string matText, string absolutePath, string targetAsset)
    {
        EchoObject mat = EchoObject.ReadFromString(matText);
        string name = TryGetStr(mat, "Name") ?? Path.GetFileNameWithoutExtension(absolutePath);

        // Shader reference: _shader.AssetID is the shader asset's GUID.
        string shaderGuid = "";
        if (mat.TryGet("_shader", out EchoObject? shader)
            && shader!.TryGet("AssetID", out EchoObject? sid)
            && sid!.TagType == EchoType.String)
        {
            shaderGuid = sid.StringValue;
        }

        // Properties: each bucket is a compound of {name: value}. Surface every entry with its bucket
        // as the category so the AI knows how to address it in a patch path.
        var props = new List<MaterialProperty>();
        if (mat.TryGet("_properties", out EchoObject? properties) && properties!.TagType == EchoType.Compound)
        {
            // Bucket short-name → friendly category. The on-disk keys are _colors, _floats, etc.
            foreach (string bucketKey in properties.GetNames())
            {
                if (!properties.TryGet(bucketKey, out EchoObject? bucket) || bucket!.TagType != EchoType.Compound)
                {
                    continue;
                }
                string category = bucketKey.TrimStart('_');
                foreach (string propName in bucket.GetNames())
                {
                    EchoObject? val = bucket.TryGet(propName, out EchoObject? v) ? v : null;
                    props.Add(new MaterialProperty(propName, category, DescribeMaterialValue(val)));
                }
            }
        }

        return new MaterialDetail(targetAsset, shaderGuid, name, props, ProvenancedDelta.ContentHash(mat));
    }

    /// <summary>Render a material property value to a compact string for the AI to read. Colors and
    /// vectors become comma-separated components; textures become their GUID; scalars inline.</summary>
    private static string DescribeMaterialValue(EchoObject? v)
    {
        if (v is null) return "NULL";
        // TryGet throws on non-compound tags, so guard every compound-only path with a TagType check.
        if (v.TagType != EchoType.Compound) return DescribeScalar(v);

        // Texture bucket entries are { AssetID: guid }.
        if (v.TryGet("AssetID", out EchoObject? aid) && aid!.TagType == EchoType.String)
        {
            return aid.StringValue;
        }
        // Color/vector buckets are compounds of named floats (R/G/B/A or X/Y/Z/W).
        var parts = new List<string>();
        foreach (string key in v.GetNames())
        {
            if (v.TryGet(key, out EchoObject? c)) parts.Add($"{key}={DescribeScalar(c)}");
        }
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static string DescribeScalar(EchoObject? v) => v switch
    {
        null => "NULL",
        { TagType: EchoType.Float } => v.FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        { TagType: EchoType.Double } => v.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        { TagType: EchoType.Int } => v.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        { TagType: EchoType.String } => v.StringValue,
        { TagType: EchoType.List } => $"[{v.List.Count} items]",
        { TagType: EchoType.Compound } => "{...}",
        _ => v.StringValue,
    };

    /// <summary>★ P2-2 — Read a scene's top-level Fog/Ambient/Skybox settings as a flat path/value list.
    /// These fields live on the Scene root (siblings of serializeObj), not on GameObjects. Each leaf
    /// scalar becomes a SceneSettingEntry whose Path is the slash-addressable pointer the AI uses in
    /// scene_settings_patch.</summary>
    public static SceneSettings ReadSceneSettings(string sceneText, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(sceneText);
        var categories = new Dictionary<string, IReadOnlyList<SceneSettingEntry>>(StringComparer.Ordinal);

        foreach (string category in new[] { "Fog", "Ambient", "Skybox" })
        {
            if (!root.TryGet(category, out EchoObject? node) || node!.TagType != EchoType.Compound)
            {
                continue;
            }
            var entries = new List<SceneSettingEntry>();
            Flatten(category, node, entries);
            categories[category.ToLowerInvariant()] = entries;
        }

        return new SceneSettings(targetAsset, categories, ProvenancedDelta.ContentHash(root));
    }

    /// <summary>Walk a compound node and emit a path/value entry for each leaf scalar. Nested compounds
    /// recurse with a slash-joined path (e.g. Fog/Color/R).</summary>
    private static void Flatten(string prefix, EchoObject node, List<SceneSettingEntry> entries)
    {
        if (node.TagType != EchoType.Compound) return;
        foreach (string key in node.GetNames())
        {
            if (key == "$id" || key == "$type") continue;
            if (!node.TryGet(key, out EchoObject? child)) continue;
            string path = $"{prefix}/{key}";
            if (child!.TagType == EchoType.Compound)
            {
                Flatten(path, child, entries);
            }
            else
            {
                entries.Add(new SceneSettingEntry(path, DescribeScalar(child)));
            }
        }
    }

    /// <summary>★ C1 — Read a generic .asset file (ScriptableObject) as a flat path/value list. The
    /// .asset root is a single object with a $type envelope + arbitrary fields; we flatten every leaf
    /// scalar (recursing into compounds) so the AI can address any field by slash-path in
    /// asset_data_patch. Reuses the same Flatten helper as scene settings.</summary>
    public static AssetDataDetail ReadAssetData(string assetText, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(assetText);
        string typeName = TryGetStr(root, "$type") ?? "(unknown $type)";
        // Trim the assembly qualifier for readability (keep the full name in the value too).
        string name = TryGetStr(root, "Name") ?? Path.GetFileNameWithoutExtension(targetAsset);

        var fields = new List<AssetDataField>();
        Flatten(string.Empty, root, fields, skipEchoMeta: true);

        return new AssetDataDetail(targetAsset, typeName, name, fields,
            ProvenancedDelta.ContentHash(root));
    }

    /// <summary>Flatten a compound into path/value entries. <paramref name="skipEchoMeta"/> excludes
    /// $id/$type/$ref (Echo serialization metadata) from the output. Lists are recursed by index
    /// (Names/0, Names/1, ...) so each element is addressable; leaf scalars are emitted directly.</summary>
    private static void Flatten(string prefix, EchoObject node, List<AssetDataField> fields, bool skipEchoMeta = false)
    {
        // Leaf scalar at the recursion frontier — emit it (covers list elements + top-level scalars).
        if (node.TagType != EchoType.Compound && node.TagType != EchoType.List)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                fields.Add(new AssetDataField(prefix, DescribeScalar(node)));
            }
            return;
        }
        // Lists: recurse into each element with an index path.
        if (node.TagType == EchoType.List)
        {
            for (int i = 0; i < node.List.Count; i++)
            {
                string elemPath = string.IsNullOrEmpty(prefix) ? $"$values/{i}" : $"{prefix}/{i}";
                Flatten(elemPath, node.List[i], fields, skipEchoMeta);
            }
            return;
        }
        // Compound: recurse into each named child.
        foreach (string key in node.GetNames())
        {
            if (skipEchoMeta && (key == "$id" || key == "$type" || key == "$ref")) continue;
            if (!node.TryGet(key, out EchoObject? child)) continue;
            string path = string.IsNullOrEmpty(prefix) ? key : $"{prefix}/{key}";
            Flatten(path, child!, fields, skipEchoMeta);
        }
    }

    private static SceneObjectNode ReadGameObject(
        EchoObject go, TagLayerResolver? tagLayer = null,
        Dictionary<int, EchoObject>? idMap = null, int maxDepth = 0)
    {
        string id = GetStr(go, "Identifier", Guid.Empty.ToString());
        string name = GetStr(go, "Name", "");
        // ★ P2-1: TagIndex/LayerIndex are indices into the project's Tags & Layers.yaml. Resolve to
        // names via TagLayerResolver (offline mirror of the runtime TagLayerManager). Keep the raw
        // indices too so the AI can round-trip (patch by index).
        int tagIndex = GetInt(go, "TagIndex", 0);
        int layerIndex = GetInt(go, "LayerIndex", 0);
        TagLayerResolver resolver = tagLayer ?? TagLayerResolver.Default;
        string tagName = resolver.GetTag(tagIndex);
        string layerName = resolver.GetLayer(layerIndex);

        SceneTransform transform = ReadTransform(go);

        // ★ Gen-P0.2 — resolve child entries (inline bodies AND {$id} stubs) and, when an id map is
        // supplied, emit the child NODES nested up to maxDepth so consumers see the whole subtree.
        var childIds = new List<string>();
        List<SceneObjectNode>? childNodes = null;
        if (go.TryGet("Children", out EchoObject? children) && children!.TagType == EchoType.List)
        {
            foreach (EchoObject child in children.List)
            {
                EchoObject? resolved = idMap is not null
                    ? ResolveFlatEntry(idMap, child)
                    : (IsFullGameObjectNode(child) ? child : null);
                if (resolved is null) continue;
                string? cid = TryGetStr(resolved, "Identifier");
                if (cid is null) continue;
                childIds.Add(cid);
                if (idMap is not null && maxDepth > 0)
                {
                    childNodes ??= new List<SceneObjectNode>();
                    childNodes.Add(ReadGameObject(resolved, tagLayer, idMap, maxDepth - 1));
                }
            }
        }

        var components = new List<SceneComponent>();
        if (go.TryGet("Components", out EchoObject? comps) && comps!.TagType == EchoType.List)
        {
            foreach (EchoObject comp in comps.List)
            {
                components.Add(ReadComponent(comp));
            }
        }

        return new SceneObjectNode(id, name, tagName, layerIndex, transform, childIds, components,
            TagIndex: tagIndex, LayerIndex: layerIndex, LayerName: layerName, Children: childNodes);
    }

    private static SceneTransform ReadTransform(EchoObject go)
    {
        if (!go.TryGet("Transform", out EchoObject? t)) return DefaultTransform();
        EchoObject pos = t!.TryGet("_localPosition", out EchoObject? p) ? p! : EchoObject.NewCompound();
        EchoObject rot = t.TryGet("_localRotation", out EchoObject? r) ? r! : EchoObject.NewCompound();
        EchoObject scl = t.TryGet("_localScale", out EchoObject? s) ? s! : EchoObject.NewCompound();
        return new SceneTransform(
            GetFloat(pos, "X", 0f), GetFloat(pos, "Y", 0f), GetFloat(pos, "Z", 0f),
            GetFloat(rot, "X", 0f), GetFloat(rot, "Y", 0f), GetFloat(rot, "Z", 0f), GetFloat(rot, "W", 1f),
            GetFloat(scl, "X", 1f), GetFloat(scl, "Y", 1f), GetFloat(scl, "Z", 1f));
    }

    private static SceneComponent ReadComponent(EchoObject comp)
    {
        // $type is the assembly-qualified name; trim to short type name for readability.
        string fullType = TryGetStr(comp, "$type") ?? "Unknown";
        string shortType = fullType;
        int comma = fullType.IndexOf(',');
        if (comma > 0) shortType = fullType[..comma].Trim();
        // Strip namespace for the friendliest name.
        int lastDot = shortType.LastIndexOf('.');
        if (lastDot >= 0) shortType = shortType[(lastDot + 1)..];

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string componentId = "";
        foreach (string key in comp.GetNames())
        {
            if (key == "$id" || key == "$type" || key == "$ref") continue;
            // ★ P0-4: hoist _identifier to the top-level Identifier field (don't duplicate in Fields).
            // ★ Gen-P0.2: editor-saved scenes store it in Echo's compact Guid form {$t:"g",$v:"…"} —
            // the old string-only read surfaced empty identifiers for every real scene.
            if (key == "_identifier")
            {
                componentId = ReadComponentIdentifier(comp) ?? "";
                continue;
            }
            fields[key] = Describe(comp.Get(key));
        }

        return new SceneComponent(shortType, fields, componentId);
    }

    // ---- value accessors (tolerant of missing/typed-wrong fields) ----

    private static string Describe(EchoObject? v)
    {
        if (v is null) return "NULL";
        return v.TagType switch
        {
            EchoType.Null => "NULL",
            EchoType.Compound or EchoType.List => "...", // nested structure summarized
            _ => v.StringValue,
        };
    }

    private static string GetStr(EchoObject o, string key, string def)
        => TryGetStr(o, key) ?? def;

    private static string? TryGetStr(EchoObject o, string key)
        => o.TryGet(key, out EchoObject? v) && v!.TagType == EchoType.String ? v.StringValue : null;

    private static int GetInt(EchoObject o, string key, int def)
        => o.TryGet(key, out EchoObject? v) && v!.TagType == EchoType.Int ? v.IntValue : def;

    private static float GetFloat(EchoObject o, string key, float def)
    {
        if (!o.TryGet(key, out EchoObject? v)) return def;
        return v!.TagType switch
        {
            EchoType.Float => v.FloatValue,
            EchoType.Double => (float)v.DoubleValue,
            _ => def,
        };
    }

    private static SceneTransform DefaultTransform()
        => new(0, 0, 0, 0, 0, 0, 1, 1, 1, 1);
}
