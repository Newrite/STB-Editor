namespace StbEditor.Data.Catalog;

public sealed record RecordTypeInfo(string Signature, string Name, string Category);

/// <summary>Record types grouped the way the Creation Kit's Object Window shows them.</summary>
public static class RecordTypes
{
    public static readonly string[] CategoryOrder =
        ["World Objects", "Items", "Actors", "Magic", "Character", "Audio", "Special Effect", "World Data", "Miscellaneous", "Other"];

    /// <summary>Every signature the catalog knows about.</summary>
    public static IEnumerable<string> Signatures => Table.Select(t => t.Signature);

    private static readonly RecordTypeInfo[] Table =
    [
        // World Objects — what gets placed in cells.
        new("ACTI", "Activator", "World Objects"),
        new("TACT", "Talking Activator", "World Objects"),
        new("CONT", "Container", "World Objects"),
        new("DOOR", "Door", "World Objects"),
        new("FLOR", "Flora", "World Objects"),
        new("FURN", "Furniture", "World Objects"),
        new("GRAS", "Grass", "World Objects"),
        new("LIGH", "Light", "World Objects"),
        new("MSTT", "Movable Static", "World Objects"),
        new("STAT", "Static", "World Objects"),
        new("SCOL", "Static Collection", "World Objects"),
        new("TREE", "Tree", "World Objects"),
        new("IDLM", "Idle Marker", "World Objects"),
        new("ADDN", "Addon Node", "World Objects"),
        new("HAZD", "Hazard", "World Objects"),
        new("TXST", "Texture Set", "World Objects"),
        new("ARTO", "Art Object", "World Objects"),

        new("AMMO", "Ammo", "Items"),
        new("ARMO", "Armor", "Items"),
        new("ARMA", "Armor Addon", "Items"),
        new("BOOK", "Book", "Items"),
        new("COBJ", "Constructible Object", "Items"),
        new("INGR", "Ingredient", "Items"),
        new("KEYM", "Key", "Items"),
        new("LVLI", "Leveled Item", "Items"),
        new("MISC", "Misc Item", "Items"),
        new("OTFT", "Outfit", "Items"),
        new("ALCH", "Potion", "Items"),
        new("SCRL", "Scroll", "Items"),
        new("SLGM", "Soul Gem", "Items"),
        new("WEAP", "Weapon", "Items"),

        new("NPC_", "Actor", "Actors"),
        new("LVLN", "Leveled Actor", "Actors"),
        new("AACT", "Action", "Actors"),
        new("BPTD", "Body Part Data", "Actors"),
        new("PERK", "Perk", "Actors"),
        new("MOVT", "Movement Type", "Actors"),

        new("ENCH", "Enchantment", "Magic"),
        new("LVSP", "Leveled Spell", "Magic"),
        new("MGEF", "Magic Effect", "Magic"),
        new("SHOU", "Shout", "Magic"),
        new("SPEL", "Spell", "Magic"),
        new("WOOP", "Word of Power", "Magic"),
        new("DUAL", "Dual Cast Data", "Magic"),

        new("CLAS", "Class", "Character"),
        new("FACT", "Faction", "Character"),
        new("HDPT", "Head Part", "Character"),
        new("EYES", "Eyes", "Character"),
        new("HAIR", "Hair", "Character"),
        new("PACK", "Package", "Character"),
        new("QUST", "Quest", "Character"),
        new("RACE", "Race", "Character"),
        new("RELA", "Relationship", "Character"),
        new("VTYP", "Voice Type", "Character"),
        new("ASTP", "Association Type", "Character"),
        new("EQUP", "Equip Slot", "Character"),
        new("SMBN", "Story Manager Branch", "Character"),
        new("SMQN", "Story Manager Quest", "Character"),
        new("SMEN", "Story Manager Event", "Character"),
        new("DIAL", "Dialog Topic", "Character"),
        new("DLBR", "Dialog Branch", "Character"),
        new("DLVW", "Dialog View", "Character"),
        new("SCEN", "Scene", "Character"),
        new("IDLE", "Idle Animation", "Character"),
        new("CSTY", "Combat Style", "Character"),

        new("ASPC", "Acoustic Space", "Audio"),
        new("MUSC", "Music Type", "Audio"),
        new("MUST", "Music Track", "Audio"),
        new("REVB", "Reverb Parameters", "Audio"),
        new("SNCT", "Sound Category", "Audio"),
        new("SNDR", "Sound Descriptor", "Audio"),
        new("SOUN", "Sound Marker", "Audio"),
        new("SOPM", "Sound Output Model", "Audio"),

        new("CAMS", "Camera Shot", "Special Effect"),
        new("CPTH", "Camera Path", "Special Effect"),
        new("DEBR", "Debris", "Special Effect"),
        new("EFSH", "Effect Shader", "Special Effect"),
        new("EXPL", "Explosion", "Special Effect"),
        new("IMGS", "Imagespace", "Special Effect"),
        new("IMAD", "Imagespace Modifier", "Special Effect"),
        new("IPCT", "Impact Data", "Special Effect"),
        new("IPDS", "Impact Data Set", "Special Effect"),
        new("LENS", "Lens Flare", "Special Effect"),
        new("MATT", "Material Type", "Special Effect"),
        new("PROJ", "Projectile", "Special Effect"),
        new("RFCT", "Visual Effect", "Special Effect"),
        new("SPGD", "Shader Particle Geometry", "Special Effect"),
        new("VOLI", "Volumetric Lighting", "Special Effect"),

        new("CLMT", "Climate", "World Data"),
        new("ECZN", "Encounter Zone", "World Data"),
        new("LGTM", "Lighting Template", "World Data"),
        new("LCTN", "Location", "World Data"),
        new("LCRT", "Location Ref Type", "World Data"),
        new("REGN", "Region", "World Data"),
        new("WATR", "Water", "World Data"),
        new("WTHR", "Weather", "World Data"),
        new("WRLD", "Worldspace", "World Data"),
        new("LTEX", "Land Texture", "World Data"),
        new("CELL", "Cell", "World Data"),
        new("NAVI", "Navmesh Info Map", "World Data"),

        new("ANIO", "Animation Object", "Miscellaneous"),
        new("COLL", "Collision Layer", "Miscellaneous"),
        new("CLFM", "Color", "Miscellaneous"),
        new("FLST", "Form List", "Miscellaneous"),
        new("GLOB", "Global", "Miscellaneous"),
        new("GMST", "Game Setting", "Miscellaneous"),
        new("KYWD", "Keyword", "Miscellaneous"),
        new("LSCR", "Load Screen", "Miscellaneous"),
        new("MATO", "Material Object", "Miscellaneous"),
        new("MESG", "Message", "Miscellaneous"),
        new("FSTP", "Footstep", "Miscellaneous"),
        new("FSTS", "Footstep Set", "Miscellaneous"),
        new("DOBJ", "Default Object Manager", "Miscellaneous"),
        new("DFOB", "Default Object", "Miscellaneous"),
        new("SKIL", "Skill", "Miscellaneous"),
        new("AVIF", "Actor Value", "Miscellaneous"),
        new("CLDC", "Caravan Deck", "Miscellaneous"),
        new("RGDL", "Ragdoll", "Miscellaneous"),
        new("STAG", "Animation Sound Tag Set", "Miscellaneous"),
        new("LAYR", "Layer", "Miscellaneous"),
        new("CMPO", "Component", "Miscellaneous"),
    ];

    private static readonly Dictionary<string, RecordTypeInfo> BySignature = Table
        .GroupBy(t => t.Signature)
        .ToDictionary(g => g.Key, g => g.First());

    public static RecordTypeInfo Get(string signature) =>
        BySignature.TryGetValue(signature, out var info) ? info : new RecordTypeInfo(signature, signature, "Other");

    /// <summary>Types that are placed in the world, i.e. can be dragged into the render window.</summary>
    public static bool IsPlaceable(string signature) => signature is
        "ACTI" or "TACT" or "CONT" or "DOOR" or "FLOR" or "FURN" or "LIGH" or "MSTT" or "STAT" or "SCOL" or "TREE"
        or "IDLM" or "ADDN" or "HAZD" or "ARTO" or "SOUN" or "AMMO" or "ARMO" or "BOOK" or "INGR" or "KEYM" or "MISC"
        or "ALCH" or "SCRL" or "SLGM" or "WEAP" or "NPC_" or "LVLN";
}
