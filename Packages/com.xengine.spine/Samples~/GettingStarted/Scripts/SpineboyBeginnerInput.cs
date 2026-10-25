using XEngine.Runtime;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 4 controller: WASD / Space / mouse → model.</summary>
public class SpineboyBeginnerInput : MonoBehaviour
{
    public SpineboyBeginnerModel? Model;

    public override void Start()
    {
        Model ??= GetComponent<SpineboyBeginnerModel>();
    }

    public override void Update()
    {
        if (Model == null)
            return;

        float horizontal = 0f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.Left))
            horizontal -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.Right))
            horizontal += 1f;
        Model.TryMove(horizontal);

        if (Input.GetMouseButton(0))
            Model.TryShoot();
        if (Input.GetMouseButtonDown(1))
            Model.StartAim();
        if (Input.GetMouseButtonUp(1))
            Model.StopAim();
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W))
            Model.TryJump();
    }
}
