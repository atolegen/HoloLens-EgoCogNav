// FollowCamera.cs
// Keeps a World Space Canvas in front of the user's view.
// Attach to HUD Canvas in DemoScene.

using UnityEngine;

namespace EgoCogNav.Demo
{
    public class FollowCamera : MonoBehaviour
    {
        [SerializeField] private float distance    = 0.6f;
        [SerializeField] private float followSpeed = 3f;
        [SerializeField] private Vector3 offset    = new Vector3(0f, -0.1f, 0f);

        private Camera cam;

        private void Start()
        {
            cam = Camera.main;
            // Snap immediately on start
            SnapToCamera();
        }

        private void LateUpdate()
        {
            if (cam == null) return;

            Vector3 targetPos = cam.transform.position
                              + cam.transform.forward * distance
                              + offset;

            transform.position = Vector3.Lerp(
                transform.position, targetPos, Time.deltaTime * followSpeed);

            transform.rotation = Quaternion.LookRotation(
                transform.position - cam.transform.position);
        }

        private void SnapToCamera()
        {
            if (cam == null) return;
            transform.position = cam.transform.position
                               + cam.transform.forward * distance
                               + offset;
            transform.rotation = Quaternion.LookRotation(
                transform.position - cam.transform.position);
        }
    }
}
