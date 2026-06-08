using UnityEngine;

// Pins the cloth to the character's shoulders so it hangs and moves like a cape.
// DefaultExecutionOrder(-50) makes this Start() run before ClothSpawner's, so the
// cloth is configured before its GPU buffers get allocated.
[DefaultExecutionOrder(-50)]
[RequireComponent(typeof(ClothSpawner))]
public class CapeAttacher : MonoBehaviour {

    [Header("Cape Shape")]
    public int   capeResolution  = 10;
    public float capeHeight      = 0.85f;
    public float backOffset      = 0.06f;
    public float shoulderYOffset = 0.16f;
    public float collarDip       = 0.06f; // how far the centre of the top row dips below the shoulders
    public int   pinSpan         = 3;     // vertices pinned at EACH shoulder; >= dim/2 pins the whole top row (straight edge)
    public float widthScale      = 1.3f;  // >1 makes the cape wider than the shoulders

    [Header("Physics")]
    public float mass  = 0.18f;
    public int   loops = 50;
    // stiff is fine since both axes start at their rest length (pRlX/pRlY)
    public float pKs   = -600f;
    public float pKd   = -30f;
    public float dKs   = -600f;
    public float dKd   = -30f;
    // high bending stiffness so the cape keeps its shape instead of collapsing into thin folds
    public float bKs   = -140f;
    public float bKd   = -12f;

    [Header("Collision")]
    public float torsoRadius = 0.17f;
    public float neckRadius  = 0.06f;

    private ClothSpawner cs;
    private int       dim;
    private int[]     pinIndices;
    private Vector3[] pinPositions;

    private Transform leftShoulder;
    private Transform rightShoulder;
    private Transform chestBone;
    private Transform neckBone;
    private Transform spineBone;
    private Transform hipsBone;
    private Transform leftThighBone;
    private Transform rightThighBone;
    private Transform characterRoot;
    private bool      firstLateUpdate = true;

    void Start() {
        cs  = GetComponent<ClothSpawner>();
        dim = capeResolution + 1;

        FindBones();
        ConfigureAndInitCloth();
    }

    //  bone discovery 

    void FindBones() {
        Walker walker = FindAnyObjectByType<Walker>();
        if (walker == null) {
            Debug.LogWarning("[CapeAttacher] No Walker component found in scene, cape will spawn at world origin.");
            return;
        }

        characterRoot = walker.transform;
        Animator anim = characterRoot.GetComponentInChildren<Animator>();

        // prefer the humanoid avatar mapping when the model has one
        if (anim != null && anim.isHuman) {
            leftShoulder  = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            rightShoulder = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            chestBone     = anim.GetBoneTransform(HumanBodyBones.UpperChest)
                         ?? anim.GetBoneTransform(HumanBodyBones.Chest);
            neckBone       = anim.GetBoneTransform(HumanBodyBones.Neck);
            spineBone      = anim.GetBoneTransform(HumanBodyBones.Spine);
            hipsBone       = anim.GetBoneTransform(HumanBodyBones.Hips);
            leftThighBone  = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            rightThighBone = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            Debug.Log($"[CapeAttacher] Humanoid bones L:{leftShoulder?.name} R:{rightShoulder?.name} Chest:{chestBone?.name} Hips:{hipsBone?.name}");
        }

        // otherwise look them up by name
        if (leftShoulder == null)
            leftShoulder = SearchBone(characterRoot, "B-shoulder.L")
                        ?? SearchBone(characterRoot, "B-shoulder.L_Skel");
        if (rightShoulder == null)
            rightShoulder = SearchBone(characterRoot, "B-shoulder.R")
                         ?? SearchBone(characterRoot, "B-shoulder.R_Skel");
        if (chestBone == null)
            chestBone = SearchBone(characterRoot, "B-chest")
                     ?? SearchBone(characterRoot, "B-chest_Skel")
                     ?? SearchBone(characterRoot, "B-spine");
        if (neckBone == null)
            neckBone = SearchBone(characterRoot, "B-neck")
                    ?? SearchBone(characterRoot, "B-neck_Skel");

        if (leftShoulder == null || rightShoulder == null)
            Debug.LogWarning("[CapeAttacher] Shoulder bones not found, falling back to hardcoded offsets.");
    }

    static Transform SearchBone(Transform root, string name) {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform result = SearchBone(child, name);
            if (result != null) return result;
        }
        return null;
    }

    //  cloth setup, runs before ClothSpawner.Start() 

    void ConfigureAndInitCloth() {
        Vector3 leftPin  = LeftPin();
        Vector3 rightPin = RightPin();
        WidenPins(ref leftPin, ref rightPin);
        float   width    = Vector3.Distance(leftPin, rightPin);

        float colStep = (width > 0.001f) ? width / capeResolution : 0.04f;
        float rowStep = capeHeight / capeResolution;

        // these get read by ClothSpawner.Start()
        cs.resolution      = capeResolution;
        cs.size            = capeHeight;   // fallback segment length, the X/Y below override it
        // separate rest length per axis so both directions start at rest, whatever the aspect ratio
        cs.segmentLengthX  = colStep;
        cs.segmentLengthY  = rowStep;
        cs.mass            = mass;
        cs.loops           = loops;
        cs.cor             = 0.05f;
        // barely any global damping, just a safety against drift
        cs.velocityDamping = 0.9998f;
        cs.pKs = pKs; cs.pKd = pKd;
        cs.dKs = dKs; cs.dKd = dKd;
        cs.bKs = bKs; cs.bKd = bKd;
        cs.dragCoefficient = 30f;
        cs.windScale       = 1f;
        cs.copyFromScene   = false;

        // pin pinSpan verts at each corner. a small span clasps the cape at the
        // shoulders only and lets the middle hang free; once the spans overlap
        // (span*2 >= dim) the whole top row is pinned and the top edge stays straight.
        int span = Mathf.Clamp(pinSpan, 1, dim);
        if (span * 2 >= dim) {
            pinIndices = new int[dim];
            for (int i = 0; i < dim; i++) pinIndices[i] = i;
        } else {
            pinIndices = new int[span * 2];
            for (int i = 0; i < span; i++) {
                pinIndices[i]        = i;             // left clasp:  columns 0 .. span-1
                pinIndices[span + i] = dim - 1 - i;   // right clasp: columns dim-1 .. dim-span
            }
        }
        pinPositions = new Vector3[pinIndices.Length];
        cs.restrained = pinIndices;

        // build the cape grid: top row spans left to right shoulder, the rest hangs down
        Vector3 rowDir  = (width > 0.001f) ? (rightPin - leftPin).normalized : Vector3.right;

        Vector3[] initPos = new Vector3[dim * dim];
        for (int y = 0; y < dim; y++)
            for (int x = 0; x < dim; x++) {
                Vector3 top = CollarPin(x, leftPin, rightPin);
                initPos[y * dim + x] = top + Vector3.down * (y * rowStep);
            }

        cs.overrideVertexPositions = initPos;

        for (int k = 0; k < pinIndices.Length; k++)
            pinPositions[k] = initPos[pinIndices[k]];

        Debug.Log($"[CapeAttacher] Configured cape: width={width:F3}m, height={capeHeight}m, " +
                  $"leftPin={leftPin}, rightPin={rightPin}");
    }

    //  runtime: drag the pinned row along with the skeleton 

    void LateUpdate() {
        if (cs == null || !cs.WasSuccessfullyInitialized()) return;

        Vector3 leftPin  = LeftPin();
        Vector3 rightPin = RightPin();
        WidenPins(ref leftPin, ref rightPin);

        // first frame: the animator has now posed the bones, so the shoulders are in
        // the right place. rebuild the layout properly (Start() ran too early and used
        // a rough guess).
        if (firstLateUpdate) {
            firstLateUpdate = false;
            float   width   = Vector3.Distance(leftPin, rightPin);
            float   colStep = (width > 0.001f) ? width / capeResolution : 0.04f;
            float   rowStep = capeHeight / capeResolution;
            Vector3 rowDir  = (width > 0.001f) ? (rightPin - leftPin).normalized : Vector3.right;
            var pos = new Vector3[dim * dim];
            for (int y = 0; y < dim; y++)
                for (int x = 0; x < dim; x++) {
                    Vector3 top = CollarPin(x, leftPin, rightPin);
                    pos[y * dim + x] = top + Vector3.down * (y * rowStep);
                }
            cs.ReinitCloth(pos, colStep, rowStep);
        }

        for (int k = 0; k < pinIndices.Length; k++)
            pinPositions[k] = CollarPin(pinIndices[k], leftPin, rightPin);

        cs.UpdatePinnedPositions(pinIndices, pinPositions);

        // collision spheres for the body, rebuilt from the bones every frame.
        // centre them on the bones (not pushed forward) so they cover the back as
        // well. the cape hangs behind the body, so spheres pushed to the front never
        // touched it and it clipped straight through.
        if (chestBone != null) {
            Vector3 neckPos = (neckBone != null) ? neckBone.position : chestBone.position + Vector3.up * 0.22f;

            Vector3 hipsPos = (hipsBone != null) ? hipsBone.position
                                                 : chestBone.position - Vector3.up * 0.4f;

            // drop the thigh spheres to mid-thigh so the cape can't slip between the legs
            Vector3 leftThighPos  = (leftThighBone  != null) ? leftThighBone.position + Vector3.down * 0.12f
                                                               : hipsPos - characterRoot.right * 0.1f;
            Vector3 rightThighPos = (rightThighBone != null) ? rightThighBone.position + Vector3.down * 0.12f
                                                              : hipsPos + characterRoot.right * 0.1f;

            float thighR = torsoRadius * 0.6f;

            cs.sphereColliders = new GPUCollision.SphereCollider[] {
                // chest, a touch oversized so it reaches past the back for the cape to rest on
                new GPUCollision.SphereCollider { center = chestBone.position, radius = torsoRadius * 1.1f },
                // hips / lower back
                new GPUCollision.SphereCollider { center = hipsPos,            radius = torsoRadius },
                // thighs, to keep the cape out from between the legs
                new GPUCollision.SphereCollider { center = leftThighPos,       radius = thighR },
                new GPUCollision.SphereCollider { center = rightThighPos,      radius = thighR },
                // neck
                new GPUCollision.SphereCollider { center = neckPos,            radius = neckRadius },
            };
        }
    }

    //  helpers: shoulder attach points, pushed slightly behind the character 

    // push the two clasp points out from their midpoint by widthScale so the cape
    // is wider than the shoulders. widthScale = 1 keeps them right on the shoulders.
    void WidenPins(ref Vector3 leftPin, ref Vector3 rightPin) {
        Vector3 mid = 0.5f * (leftPin + rightPin);
        leftPin  = mid + (leftPin  - mid) * widthScale;
        rightPin = mid + (rightPin - mid) * widthScale;
    }

    // world position of top-row pin x, with a parabolic dip so the collar curves down in the middle
    Vector3 CollarPin(int x, Vector3 leftPin, Vector3 rightPin) {
        float t   = (float)x / (dim - 1);
        Vector3 p = Vector3.Lerp(leftPin, rightPin, t);
        float dip = 4f * t * (1f - t) * collarDip;   // parabola: 0 at edges, collarDip at centre
        Vector3 up = characterRoot != null ? characterRoot.up : Vector3.up;
        return p - up * dip;
    }

    Vector3 LeftPin() {
        if (leftShoulder != null && characterRoot != null)
            return leftShoulder.position
                + characterRoot.up      * shoulderYOffset
                - characterRoot.forward * backOffset;

        Vector3 root = characterRoot != null ? characterRoot.position : Vector3.zero;
        Vector3 fwd  = characterRoot != null ? characterRoot.forward  : Vector3.forward;
        Vector3 left = characterRoot != null ? -characterRoot.right   : Vector3.left;
        return root + Vector3.up * 1.4f + left * 0.2f - fwd * backOffset;
    }

    Vector3 RightPin() {
        if (rightShoulder != null && characterRoot != null)
            return rightShoulder.position
                + characterRoot.up      * shoulderYOffset
                - characterRoot.forward * backOffset;

        Vector3 root  = characterRoot != null ? characterRoot.position : Vector3.zero;
        Vector3 fwd   = characterRoot != null ? characterRoot.forward  : Vector3.forward;
        Vector3 right = characterRoot != null ? characterRoot.right    : Vector3.right;
        return root + Vector3.up * 1.4f + right * 0.2f - fwd * backOffset;
    }
}

