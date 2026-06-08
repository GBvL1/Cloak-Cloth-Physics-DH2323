using UnityEngine;

public class Walker : MonoBehaviour {

    [Header("Movement")]
    public float walkSpeed   = 2f;
    public float runSpeed    = 5f;
    public float rotateSpeed = 90f;

    [Header("Jump")]
    public float jumpHeight = 1.5f;
    public float gravity    = -15f;

    private Animator anim;
    private float    yVelocity;
    private float    groundY;

    void Start() {
        anim    = GetComponent<Animator>();
        groundY = transform.position.y;
        if (anim != null) anim.applyRootMotion = false;
    }

    void Update() {
        float v       = Input.GetAxis("Vertical");
        float h       = Input.GetAxis("Horizontal");
        bool  running = Input.GetKey(KeyCode.LeftShift);
        bool  grounded = transform.position.y <= groundY + 0.01f;

        // Horizontal locomotion
        float speed = running ? runSpeed : walkSpeed;
        transform.position += transform.forward * v * speed * Time.deltaTime;
        transform.Rotate(Vector3.up * h * rotateSpeed * Time.deltaTime);

        // Jump input
        if (grounded && yVelocity <= 0f && Input.GetKeyDown(KeyCode.Space)) {
            yVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            anim.SetTrigger("Jump");
        }

        // Gravity
        yVelocity += gravity * Time.deltaTime;
        transform.position += Vector3.up * yVelocity * Time.deltaTime;

        // Ground clamp
        if (transform.position.y < groundY) {
            Vector3 p = transform.position;
            p.y = groundY;
            transform.position = p;
            if (yVelocity < 0f) yVelocity = 0f;
        }

        // Feed animator
        // Speed: -2 run back, -1 walk back, 0 idle, 1 walk fwd, 2 run fwd
        float animSpeed = v * (running ? 2f : 1f);
        anim.SetFloat("Speed", animSpeed, 0.1f, Time.deltaTime);
        anim.SetBool("IsGrounded", grounded);
    }
}
