//
// DEFINES
//

#define EPSILON 0.001



//
// TYPES
//

struct Ray {
	float3 origin, direction;
};

struct SphereCollider {
	float3 center;
	float radius;
};

struct BoxCollider {
	float3 center;
	float3 extents;
};

struct Hit {
	bool collision;
	float3 hitPoint,  hitNormal;
};


//
// UTILITY METHODS
//

float3 NearestPointOnRay(Ray r, float3 p) {
	float lenSq = dot(r.direction, r.direction);
	// ternary so t is always initialised, otherwise Metal warns about an
	// uninitialized variable and can give garbage collisions on Apple Silicon.
	float t = (lenSq >= EPSILON * EPSILON)
	          ? saturate(dot(p - r.origin, r.direction) / lenSq)
	          : 0.0;
	return r.origin + t * r.direction;
}

float3 Reflect(float3 original, float3 normal) {
	return original - 2.0*dot(original, normal)*normal;
}



//
// COLLISIONS
//

/* Ray-sphere intersection. Catches the particle's path passing through the
   sphere and pushes it out to the surface so it can't tunnel through. The
   normalize guard below avoids a NaN on Metal. */
Hit RaySphereCollision(Ray r, SphereCollider s, float padding) {
	Hit h;
	h.collision = false;
	h.hitPoint  = float3(0, 0, 0);
	h.hitNormal = float3(0, 1, 0);

	float3 nearestPoint = NearestPointOnRay(r, s.center);
	float3 diff         = nearestPoint - s.center;
	float  dist         = length(diff);

	if (dist <= s.radius + padding) {
		h.collision = true;
		// normalize, but fall back to world-up if we're exactly at the centre
		h.hitNormal = (dist > EPSILON) ? (diff / dist) : float3(0, 1, 0);
		// push out to the sphere surface, not just the nearest point on the ray
		h.hitPoint  = s.center + h.hitNormal * (s.radius + padding + EPSILON);
	}

	return h;
}

Hit RayBoxCollision(Ray r, BoxCollider b) {
	Hit h;
	h.collision = false;
	h.hitPoint = float3(0, 0, 0);
	h.hitNormal = h.hitPoint;




	return h;
}
