using UnityEngine;

public class FollowCamera : MonoBehaviour {

    [Header("Target")]
    public Transform target;

    [Header("Orbit")]
    public float orbitSensitivity = 3f;
    public float returnSpeed      = 2f;   // how fast the camera drifts back behind the character
    public float minPitch         = -20f;
    public float maxPitch         = 60f;

    [Header("Distance")]
    public float distance = 5f;
    public float minZoom  = 2f;
    public float maxZoom  = 12f;
    public float scrollSpeed = 4f;

    [Header("Position")]
    public float height    = 1.5f;  // pivot height above character root
    public float smoothTime = 0.15f;

    private float   yaw;
    private float   pitch = 15f;
    private Vector3 velocity;

    void Start() {
        Walker walker = FindAnyObjectByType<Walker>();
        if (walker != null) target = walker.transform;

        if (target != null) yaw = target.eulerAngles.y;
    }

    void LateUpdate() {
        if (target == null) return;

        //  Zoom 
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        distance = Mathf.Clamp(distance - scroll * scrollSpeed, minZoom, maxZoom);

        //  Orbit (right-click drag) 
        if (Input.GetMouseButton(1)) {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
            yaw   += Input.GetAxis("Mouse X") * orbitSensitivity;
            pitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
        } else {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
            // Gently drift back to face the character's forward
            yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, Time.deltaTime * returnSpeed);
        }

        // Position 
        Quaternion rot     = Quaternion.Euler(pitch, yaw, 0f);
        Vector3    pivot   = target.position + Vector3.up * height;
        Vector3    desired = pivot + rot * new Vector3(0f, 0f, -distance);

        transform.position = Vector3.SmoothDamp(transform.position, desired, ref velocity, smoothTime);
        transform.LookAt(pivot);
    }
}
