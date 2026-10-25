using Spine;

using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.OtherExamples;

/// <summary>Combine Mix-and-Match skins. H/E/N cycle hair/eyes/nose; C/P cycle clothes/pants.</summary>
public class MixAndMatchSkinsExample : MonoBehaviour
{
    public string BaseSkin = "skin-base";
    public string EyelidsSkin = "eyelids/girly";
    public string[] HairSkins = ["hair/brown", "hair/blue", "hair/pink", "hair/short-red", "hair/long-blue-with-scarf"];
    public int ActiveHairIndex;
    public string[] EyesSkins = ["eyes/violet", "eyes/green", "eyes/yellow"];
    public int ActiveEyesIndex;
    public string[] NoseSkins = ["nose/short", "nose/long"];
    public int ActiveNoseIndex;
    public string[] ClothesSkins = ["clothes/hoodie-orange", "clothes/hoodie-blue-and-scarf", "clothes/dress-blue"];
    public int ActiveClothesIndex;
    public string[] PantsSkins = ["legs/pants-jeans", "legs/pants-green", "legs/boots-red"];
    public int ActivePantsIndex;
    public string HatSkin = "accessories/hat-red-yellow";
    public string BagSkin = "";
    public SkeletonAnimation? SkeletonAnimation;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        SkeletonAnimation?.Initialize(overwrite: false);
        ApplyCombined();
    }

    public override void Update()
    {
        if (Input.GetKeyDown(KeyCode.H))
            NextHair();
        if (Input.GetKeyDown(KeyCode.E))
            NextEyes();
        if (Input.GetKeyDown(KeyCode.N))
            NextNose();
        if (Input.GetKeyDown(KeyCode.C))
            NextClothes();
        if (Input.GetKeyDown(KeyCode.P))
            NextPants();
    }

    public void NextHair()
    {
        ActiveHairIndex = Next(ActiveHairIndex, HairSkins.Length);
        ApplyCombined();
    }

    public void NextEyes()
    {
        ActiveEyesIndex = Next(ActiveEyesIndex, EyesSkins.Length);
        ApplyCombined();
    }

    public void NextNose()
    {
        ActiveNoseIndex = Next(ActiveNoseIndex, NoseSkins.Length);
        ApplyCombined();
    }

    public void NextClothes()
    {
        ActiveClothesIndex = Next(ActiveClothesIndex, ClothesSkins.Length);
        ApplyCombined();
    }

    public void NextPants()
    {
        ActivePantsIndex = Next(ActivePantsIndex, PantsSkins.Length);
        ApplyCombined();
    }

    public void ApplyCombined()
    {
        Skeleton? skeleton = SkeletonAnimation?.Skeleton;
        if (skeleton == null)
            return;

        SpineSkinMixer.Apply(skeleton,
            BaseSkin,
            Pick(NoseSkins, ActiveNoseIndex),
            EyelidsSkin,
            Pick(EyesSkins, ActiveEyesIndex),
            Pick(HairSkins, ActiveHairIndex),
            Pick(ClothesSkins, ActiveClothesIndex),
            Pick(PantsSkins, ActivePantsIndex),
            HatSkin,
            BagSkin);
    }

    private static int Next(int index, int length)
        => length <= 0 ? 0 : (index + 1) % length;

    private static string Pick(string[] names, int index)
    {
        if (names == null || names.Length == 0)
            return "";
        if (index < 0 || index >= names.Length)
            return names[0];
        return names[index];
    }
}
