using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ClothSpawner : MonoBehaviour {

	public enum IntegrationMethod {EULARIAN, LEAPFROG};

	// Simulation settings.
	public Vector3 positionOffset = Vector3.zero;
	public IntegrationMethod method = IntegrationMethod.EULARIAN;  // Not changeable at runtime.
	public int resolution = 25;  // Not changeable at runtime.
	public float size = 10f;  // Not changeable at runtime.
	public int loops = 500;
	public float cor = 0.1f;
	public float mass = 1.0f;
	// per-substep velocity multiplier, bleeds a little energy for stability.
	public float velocityDamping = 0.999f;

	// parallel spring parameters
	public float pScale = 1.0f;
	public float pKs = -10000.0f;
	public float pKd = -1000.0f;

	// Diagonal spring parameters.
	public float dScale = 1.0f;
	public float dKs = -10000.0f;
	public float dKd = -1000.0f;

	// Bending spring parameters.
	public float bScale = 1.0f;
	public float bKs = -10000.0f;
	public float bKd = -1000.0f;

	// Wind / drag parameters.
	public float windScale = 1.0f;
	public float dragCoefficient = 50.0f;
	public Vector3 windVelocity = Vector3.zero;

	// Restrained vertex ids.
	public int[] restrained;

	// Mesh and mesh arrays.
	private Mesh mesh;
	private Vector3[] vertices;
	private int[] triangles;

	// Compute shader and kernels.
	public ComputeShader clothCompute;
	private int springKernel;
	private int dragKernel;
	private int integrateKernel;

	// Compute buffers.
	private ComputeBuffer positionBuffer;
	private ComputeBuffer velocityBuffer;
	private ComputeBuffer forceBuffer;
	private ComputeBuffer restrainedBuffer;
	private ComputeBuffer triangleBuffer;

	// if set before Start(), used instead of the generated grid.
	[HideInInspector] public Vector3[] overrideVertexPositions = null;
	// separate rest lengths for a non-square cloth (set by CapeAttacher). 0 means use segmentLength.
	[HideInInspector] public float segmentLengthX = 0f;
	[HideInInspector] public float segmentLengthY = 0f;

	// Other.
	private int count;  // total number of nodes.
	private int dim;  // number of nodes per row in cloth.
	private float segmentLength;  // length of one square of cloth.
	private float cachedPRlX, cachedPRlY;  // stored for UpdateSimulation to reuse.
	private Vector3[] zeros;
	private bool successfullyInitialized = false;

	// Collision.
	public bool copyFromScene;
	public GPUCollision.SphereCollider[] sphereColliders;
	private ComputeBuffer sphereBuffer;
	private int sphereCount = 0;

	public void Recalculate() {
		// Update the resolution-related parameters.
		dim = resolution + 1;
		count = dim*dim;

		segmentLength = size / (float)resolution;
		
		// Recalculate the vertices array to be drawn as gizmos.
		vertices = new Vector3[count];
		for (int y = 0; y < dim; y++) {
			for (int x = 0; x < dim; x++) {
				vertices[y*dim+x] = new Vector3((float)x*segmentLength, 0f, (float)y*segmentLength) + positionOffset;
			}
		}
	}



	void Awake() {
		// Populate vertices so editor gizmos are visible before play mode.
		Recalculate();
	}



	void Start() {
		clothCompute = (ComputeShader)Instantiate(Resources.Load("ClothCompute"));

		// Recalculate with values that may have been overridden by CapeAttacher.Awake().
		Recalculate();

		// Allow external scripts (e.g. CapeAttacher) to supply custom initial positions.
		if (overrideVertexPositions != null && overrideVertexPositions.Length == count)
			System.Array.Copy(overrideVertexPositions, vertices, count);

		// Create and set the mesh.
		mesh = CreateMesh("ClothMesh");
		GetComponent<MeshFilter>().mesh = mesh;

		// Create and set the position buffer.
		positionBuffer = new ComputeBuffer(count, 12);
		positionBuffer.SetData(vertices);

		// Create the zeros array.
		zeros = new Vector3[count];
		for (int i = 0; i < count; i++) {
			zeros[i] = Vector3.zero;
		}

		// Create and set the velocity buffer.
		velocityBuffer = new ComputeBuffer(count, 12);
		velocityBuffer.SetData(zeros);

		// Create and set the force buffer.
		forceBuffer = new ComputeBuffer(count, 12);
		forceBuffer.SetData(zeros);

		// Guard against null restrained array (e.g. when set programmatically at runtime).
		if (restrained == null) restrained = new int[0];

		// Create an array representing which vertices to hold fixed.
		int[] restrainedArray = new int[count];
		for (int i = 0; i < count; i++) {
			restrainedArray[i] = (System.Array.Exists(restrained, element => element == i)) ? 1 : 0;
		}

		// Create and set the restrained buffer.
		restrainedBuffer = new ComputeBuffer(count, 4);
		restrainedBuffer.SetData(restrainedArray);

		// Create an array to hold the triangles as integer vectors.
		Vector3Int[] triangleArray = new Vector3Int[triangles.Length/3];
		for (int i = 0; i < triangles.Length; i += 3) {
			triangleArray[i/3] = new Vector3Int(triangles[i], triangles[i+1], triangles[i+2]);
		}

		// Create and set the triangle buffer.
		triangleBuffer = new ComputeBuffer(triangleArray.Length, 12);
		triangleBuffer.SetData(triangleArray);

		// Get the kernels from the compute shader.
		springKernel = clothCompute.FindKernel("Spring");
		dragKernel = clothCompute.FindKernel("Drag");
		integrateKernel = clothCompute.FindKernel("Integrate");

		// Upload the buffers to the gpu, and make them available to each kernel.
		clothCompute.SetBuffer(springKernel, "positionBuffer", positionBuffer);
		clothCompute.SetBuffer(springKernel, "velocityBuffer", velocityBuffer);
		clothCompute.SetBuffer(springKernel, "forceBuffer", forceBuffer);

		clothCompute.SetBuffer(dragKernel, "positionBuffer", positionBuffer);
		clothCompute.SetBuffer(dragKernel, "velocityBuffer", velocityBuffer);
		clothCompute.SetBuffer(dragKernel, "forceBuffer", forceBuffer);
		clothCompute.SetBuffer(dragKernel, "triangleBuffer", triangleBuffer);  // only need triangles for drag calculations.

		clothCompute.SetBuffer(integrateKernel, "positionBuffer", positionBuffer);
		clothCompute.SetBuffer(integrateKernel, "velocityBuffer", velocityBuffer);
		clothCompute.SetBuffer(integrateKernel, "forceBuffer", forceBuffer);
		clothCompute.SetBuffer(integrateKernel, "restrainedBuffer", restrainedBuffer);  // only need fixed vertices during integration phase.

		// bind a dummy sphere buffer so Integrate always has something bound.
		// on Metal/Vulkan an unbound buffer makes the whole dispatch silently fail.
		sphereBuffer = new ComputeBuffer(1, GPUCollision.SphereColliderSize());
		sphereBuffer.SetData(new GPUCollision.SphereCollider[1]);
		clothCompute.SetInt("sphereCount", 0);
		clothCompute.SetBuffer(integrateKernel, "sphereBuffer", sphereBuffer);

		// Upload the constant parameters to the gpu.
		clothCompute.SetInt("count", count);
		clothCompute.SetInt("dim", dim);
		clothCompute.SetInt("triangleCount", triangleArray.Length);

		cachedPRlX = (segmentLengthX > 0f) ? segmentLengthX : segmentLength;
		cachedPRlY = (segmentLengthY > 0f) ? segmentLengthY : segmentLength;
		float dRl  = Mathf.Sqrt(cachedPRlX * cachedPRlX + cachedPRlY * cachedPRlY);
		float bRl  = 2f * dRl;
		clothCompute.SetFloat("pRlX", cachedPRlX);
		clothCompute.SetFloat("pRlY", cachedPRlY);
		clothCompute.SetFloat("dRl", dRl);
		clothCompute.SetFloat("bRl", bRl);

		clothCompute.SetInt("euler", 1);

		if (method == IntegrationMethod.LEAPFROG) {
			// compute half-step ahead positions by eulerian integration for leapfrog method.

			// Update the positions to a half time step
			UpdateSimulation(1, Time.deltaTime*0.5f);

			// Clear out the velocity buffer to "reset" it, since the velocity is advanced first in
			// leapfrog method.
			velocityBuffer.SetData(zeros);

			clothCompute.SetInt("euler", 0);
		}

		// Initialization was successful.
		successfullyInitialized = true;
	}



	// average cloth position. used as the bounds centre so frustum culling follows
	// the cape as the character walks off, otherwise the bounds stay at the spawn
	// point and the cape gets culled once that leaves view. skips NaNs so one bad
	// frame can't poison the average.
	private Vector3 ClothCenter() {
		Vector3 sum = Vector3.zero;
		int n = 0;
		for (int i = 0; i < count; i++) {
			Vector3 v = vertices[i];
			if (!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)) {
				sum += v;
				n++;
			}
		}
		return (n > 0) ? sum / n : Vector3.zero;
	}

	// moves specific verts in the GPU buffer and the mesh. CapeAttacher calls this
	// every LateUpdate to keep the pinned row on the skeleton.
	public void UpdatePinnedPositions(int[] indices, Vector3[] worldPositions) {
		if (!successfullyInitialized || positionBuffer == null) return;
		int n = Mathf.Min(indices.Length, worldPositions.Length);
		for (int i = 0; i < n; i++) {
			int idx = indices[i];
			if (idx >= 0 && idx < count)
				vertices[idx] = worldPositions[i];
		}
		positionBuffer.SetData(vertices);
		mesh.vertices = vertices;
		mesh.RecalculateNormals();
		// big fixed-size bounds centred on the cape. RecalculateBounds() can go NaN on
		// a bad frame and cull the mesh; this avoids that and still follows the cape.
		mesh.bounds = new Bounds(ClothCenter(), Vector3.one * 20f);
	}



	void UpdateSimulation(int t, float dt) {
		// Update the dynamic simulation variables.
		clothCompute.SetFloat("mass", mass);
		clothCompute.SetFloat("cor", cor);
		clothCompute.SetFloat("dt", dt/(float)t);
		clothCompute.SetFloat("velocityDamping", velocityDamping);

		clothCompute.SetFloat("windScale", windScale);
		clothCompute.SetFloat("dragCoefficient", dragCoefficient);
		clothCompute.SetVector("windVelocity", windVelocity);

		clothCompute.SetFloat("pRlX", cachedPRlX);
		clothCompute.SetFloat("pRlY", cachedPRlY);
		clothCompute.SetFloat("pScale", pScale);
		clothCompute.SetFloat("pKs", pKs);
		clothCompute.SetFloat("pKd", pKd);

		clothCompute.SetFloat("dScale", dScale);
		clothCompute.SetFloat("dKs", dKs);
		clothCompute.SetFloat("dKd", dKd);
			
		clothCompute.SetFloat("bScale", bScale);
		clothCompute.SetFloat("bKs", bKs);
		clothCompute.SetFloat("bKd", bKd);

		// Reset sphereColliders array from scene, if enabled.
		if (copyFromScene) {
			SphereCollider[] inScene = FindObjectsOfType<SphereCollider>();
			sphereColliders = new GPUCollision.SphereCollider[inScene.Length];
			for (int i = 0; i < inScene.Length; i++) {
				sphereColliders[i].center = inScene[i].transform.TransformPoint(inScene[i].center);
				sphereColliders[i].radius = inScene[i].radius;
			}
		}

		// only reallocate the sphere buffer when the count changes, otherwise just
		// re-upload. releasing and recreating it every frame stalls the GPU on Metal.
		int newSphereCount = (sphereColliders != null) ? sphereColliders.Length : 0;
		if (newSphereCount != sphereCount) {
			sphereCount = newSphereCount;
			if (sphereBuffer != null) sphereBuffer.Release();
			int bufSize = Mathf.Max(1, sphereCount);
			sphereBuffer = new ComputeBuffer(bufSize, GPUCollision.SphereColliderSize());
			clothCompute.SetBuffer(integrateKernel, "sphereBuffer", sphereBuffer);
		}
		if (sphereCount > 0)
			sphereBuffer.SetData(sphereColliders);
		clothCompute.SetInt("sphereCount", sphereCount);

		// Advance the simulation n times.
		for (int i = 0; i < t; i++) {
			// Clear the force buffer.
			forceBuffer.SetData(zeros);

			// Accumulate the spring and drag forces.
			clothCompute.Dispatch(springKernel, count/256+1, 1, 1);
			clothCompute.Dispatch(dragKernel, (triangles.Length/3)/256+1, 1, 1);

			// Update the simulation.
			clothCompute.Dispatch(integrateKernel, count/256+1, 1, 1);
		}
	}



	void FixedUpdate() {
		if (successfullyInitialized) {
			UpdateSimulation(loops, Time.deltaTime);

			positionBuffer.GetData(vertices);

			// reset if any free vertex went NaN or shot off into the distance.
			bool needsReset = false;
			for (int i = dim; i < count; i++) {
				Vector3 v = vertices[i];
				if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || v.sqrMagnitude > 250000f) {
					needsReset = true;
					break;
				}
			}
			if (needsReset) {
				// rebuild a plain hanging pose: pinned row stays put, each row offset down.
				for (int y = 1; y < dim; y++)
					for (int x = 0; x < dim; x++)
						vertices[y * dim + x] = vertices[x] + Vector3.down * (cachedPRlY * y);
				positionBuffer.SetData(vertices);
				velocityBuffer.SetData(zeros);
				Debug.LogWarning("[ClothSpawner] Simulation reset (NaN or escaped vertex).");
			}

			mesh.vertices = vertices;
			mesh.RecalculateNormals();
			mesh.bounds = new Bounds(ClothCenter(), Vector3.one * 20f);
		}
	}



	void OnDestroy() {
		if (positionBuffer != null) {
			positionBuffer.Release();
		}

		if (velocityBuffer != null) {
			velocityBuffer.Release();
		}

		if (forceBuffer != null) {
			forceBuffer.Release();
		}

		if (restrainedBuffer != null) {
			restrainedBuffer.Release();
		}

		if (triangleBuffer != null) {
			triangleBuffer.Release();
		}

		if (sphereBuffer != null) {
			sphereBuffer.Release();
		}
	}



	void OnDrawGizmos() {
		if (vertices != null && !successfullyInitialized) {
			for (int i = 0; i < vertices.Length; i++) {
				if (restrained != null && System.Array.Exists(restrained, element => element == i)) {
					Gizmos.color = Color.red;
					Gizmos.DrawSphere(vertices[i], segmentLength*0.25f);
				}
				else {
					Gizmos.color = Color.white;
					Gizmos.DrawSphere(vertices[i], segmentLength*0.125f);
				}
			}
		}

		if (sphereColliders != null) {
			foreach (GPUCollision.SphereCollider s in sphereColliders) {
				Gizmos.DrawWireSphere(s.center, s.radius);
			}
		}
	}




	private Mesh CreateMesh(string name) {
		Mesh mesh = new Mesh();
		mesh.name = name;
	
		mesh.vertices = vertices;

		triangles = new int[resolution*resolution * 6];
		for (int ti = 0, vi = 0, y = 0; y < resolution; y++, vi++) {
			for (int x = 0; x < resolution; x++, ti += 6, vi++) {
				triangles[ti] = vi;
				triangles[ti + 3] = triangles[ti + 2] = vi + 1;
				triangles[ti + 4] = triangles[ti + 1] = vi + resolution + 1;
				triangles[ti + 5] = vi + resolution + 2;
			}
		}
		mesh.triangles = triangles;

		Vector2[] uvs = new Vector2[vertices.Length];
		for (int y = 0; y < dim; y++) {
			for (int x = 0; x < dim; x++) {
				float u = (float)x/(float)resolution;
				float v = (float)y/(float)resolution;
				uvs[y*dim+x] = new Vector2(u, v);
			}
		}
		mesh.uv = uvs;
		
		mesh.RecalculateNormals();
		return mesh;
	}

	// reset positions and spring rest lengths once the bones are posed correctly
	// (CapeAttacher calls this on the first LateUpdate).
	public void ReinitCloth(Vector3[] positions, float rlX, float rlY) {
		if (!successfullyInitialized || positionBuffer == null) return;
		System.Array.Copy(positions, vertices, Mathf.Min(positions.Length, count));
		positionBuffer.SetData(vertices);
		velocityBuffer.SetData(zeros);
		cachedPRlX = rlX;
		cachedPRlY = rlY;
		float dRl = Mathf.Sqrt(rlX * rlX + rlY * rlY);
		clothCompute.SetFloat("pRlX", cachedPRlX);
		clothCompute.SetFloat("pRlY", cachedPRlY);
		clothCompute.SetFloat("dRl", dRl);
		clothCompute.SetFloat("bRl", 2f * dRl);
	}

	public bool WasSuccessfullyInitialized() {
		return successfullyInitialized;
	}
}