﻿using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

public class SketchToMesh : MonoBehaviour
{
	public enum ShapeType
	{
		Box = 0,
		Sphere = 1,
		Capsule = 2,
		TriangularPrism = 3,
		Pyramid = 4
	}

	#region UI
	public void ToggleObstruction(bool value)
	{
		avoidObstruction = value;
	}
	public void ToggleForcedShape(bool value)
	{
		forcedShape = value;
	}
	public void Dropdown(int value)
	{
		shapeToBuild = (ShapeType)value;
	}
	public void InputField(string value)
	{
		if (int.TryParse(value, out int depth))
		{
			shapeDepth = depth;
		}
		else
		{
			Debug.LogWarning("Depth could not be parsed.");
		}
	}
	#endregion

	[Header("References")]
	[SerializeField] private SketchingTool sketchingTool;
	[SerializeField] private RawImage rawImage;
	[Space(8)]
	[SerializeField] private Transform meshContainer;
	[SerializeField] private Material meshMaterial;
	private Camera mainCamera;

	[Header("Global Settings")]
	[SerializeField] private ShapeType shapeToBuild = ShapeType.Box;
	[Space(8)]
	[SerializeField] private float meshDistance = 5f;
	[SerializeField] private float shapeDepth = 1f;
	[SerializeField] float visibilityOffset = 0.25f;
	[SerializeField] private LayerMask raycastMask = ~0;
	[SerializeField] bool avoidObstruction = true;
	[SerializeField] bool forcedShape = true;

	private float currentDistance;

	private void Awake()
	{
		mainCamera = Camera.main;
	}

	public void GenerateMeshFromSketch()
	{
		Texture2D sketchTex = sketchingTool.GetFinalTexture();
		if (sketchTex == null) return;

		(List<Vector2Int> absCorners, List<Vector2Int> outlinePixels) = FindAbsoluteCorners(sketchTex);
		if (absCorners == null) return;
		List<Vector2Int> corners = FindCorners(sketchTex, absCorners, outlinePixels);

		if (!forcedShape) shapeToBuild = PredictShape(sketchTex, absCorners, outlinePixels);

		Vector2Int centerPx = new Vector2Int(
			(absCorners[0].x + absCorners[3].x) >> 1,
			(absCorners[0].y + absCorners[3].y) >> 1
		);

		currentDistance = GetRaycastDistance(centerPx);

		float scaledDepth = shapeDepth * (currentDistance / meshDistance);
		Vector3 centerPoint = FindCenterPoint(absCorners, currentDistance);

		GameObject generatedMeshObject = new GameObject(shapeToBuild.ToString() + " Sketch");
		generatedMeshObject.transform.SetParent(meshContainer, true);
		generatedMeshObject.transform.position = FindCenterPoint(absCorners, currentDistance);
		MeshFilter mf = generatedMeshObject.AddComponent<MeshFilter>();
		MeshRenderer mr = generatedMeshObject.AddComponent<MeshRenderer>();
		mr.material = meshMaterial;

		Mesh shapeMesh = null;
		switch (shapeToBuild)
		{
			case ShapeType.Box:
				shapeMesh = BuildBoxMesh(absCorners, scaledDepth, centerPoint);
				break;

			case ShapeType.Sphere:
				shapeMesh = BuildSphereMesh(absCorners, centerPoint);
				break;

			case ShapeType.Capsule:
				shapeMesh = BuildCapsuleMesh(absCorners, centerPoint);
				break;

			case ShapeType.TriangularPrism:
				shapeMesh = BuildTriangularPrismMesh(corners, scaledDepth, centerPoint);
				break;

			case ShapeType.Pyramid:
				shapeMesh = BuildPyramidMesh(corners, centerPoint);
				break;
		}

		mf.mesh = shapeMesh;
		AddCollider(generatedMeshObject, shapeMesh);
	}

	#region Texture2D Decoding
	private (List<Vector2Int>, List<Vector2Int>) FindAbsoluteCorners(Texture2D tex)
	{
		List<Vector2Int> corners = new List<Vector2Int>();
		List<Vector2Int> outlinePixels = new List<Vector2Int>();
		Vector2Int minPixel = new Vector2Int(tex.width, tex.height);
		Vector2Int maxPixel = new Vector2Int(0, 0);

		Color32[] pixels = tex.GetPixels32();
		int w = tex.width;
		int h = tex.height;

		bool foundAny = false;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if (pixels[y * w + x].a > 0)
				{
					outlinePixels.Add(new Vector2Int(x,y));
					foundAny = true;
					if (x < minPixel.x) minPixel.x = x;
					if (y < minPixel.y) minPixel.y = y;
					if (x > maxPixel.x) maxPixel.x = x;
					if (y > maxPixel.y) maxPixel.y = y;
				}
			}
		}

		if (!foundAny) return (null, null);

		// Include the final pixel row/col so we capture the entire range.
		maxPixel.x += 1;
		maxPixel.y += 1;

		corners.Add(new Vector2Int(minPixel.x, minPixel.y)); // bottom-left
		corners.Add(new Vector2Int(maxPixel.x, minPixel.y)); // bottom-right
		corners.Add(new Vector2Int(minPixel.x, maxPixel.y)); // top-left
		corners.Add(new Vector2Int(maxPixel.x, maxPixel.y)); // top-right

		return (corners, outlinePixels);
	}
	
	private List<Vector2Int> FindCorners(Texture2D tex, List<Vector2Int> absCorners, List<Vector2Int> outlinePixels)
	{
		List<Vector2Int> corners = new List<Vector2Int>();
		Vector2Int minPixel = absCorners[0];
		Vector2Int maxPixel = absCorners[absCorners.Count-1];
		int w = tex.width; int h = tex.height;

		Vector2 center = new Vector2(
			(minPixel.x + maxPixel.x) / 2f,
			(minPixel.y + maxPixel.y) / 2f
		);

		outlinePixels.Sort((a, b) =>
			Vector2.Distance(b, center).CompareTo(Vector2.Distance(a, center))
		);

		// Find the first corner - furthest point from center
		Vector2Int firstCorner = outlinePixels[0];
		corners.Add(firstCorner);

		// Find the second corner - farthest from the first corner
		Vector2Int secondCorner = outlinePixels
			.OrderByDescending(p => Vector2.Distance(p, firstCorner))
			.First();
		corners.Add(secondCorner);

		// Find the third corner - combination of distance from first two corners
		Vector2Int thirdCorner = outlinePixels
			.Where(p => !p.Equals(firstCorner) && !p.Equals(secondCorner))
			.OrderByDescending(p => {
				float distFromFirst = Vector2.Distance(p, firstCorner);
				float distFromSecond = Vector2.Distance(p, secondCorner);
				float combinedDist = distFromFirst + distFromSecond;

				Vector2 v1 = firstCorner - (Vector2)p;
				Vector2 v2 = secondCorner - (Vector2)p;
				float crossMagnitude = Mathf.Abs(v1.x * v2.y - v1.y * v2.x);

				return combinedDist * 0.3f + crossMagnitude;
			})
			.First();
		corners.Add(thirdCorner);

		return corners;
	}

	ShapeType PredictShape(Texture2D tex, List<Vector2Int> abs, List<Vector2Int> outline)
	{
		int c = ApproxCornerCount(outline);
		int w = abs[1].x - abs[0].x, h = abs[2].y - abs[0].y;
		float ar = w / (float)h, fill = outline.Count / ((float)w * h);
		if (c == 3) return ShapeType.TriangularPrism;       // or Pyramid
		if (c == 4) return ShapeType.Box;
		if (fill > 0.55f) return (ar > 1.2f || ar < 0.83f) ? ShapeType.Capsule : ShapeType.Sphere;

		print(c);
		return ShapeType.Sphere;
	}

	#region Helpers
	int ApproxCornerCount(List<Vector2Int> outline)
	{
		var hull = ConvexHull(outline);                       // already defined
		float angT = 30f;                                     // deg. change that signals a corner
		float distT = sketchingTool.BrushSize * 2f;           // merge radius (≈ stroke width)
		List<Vector2> cand = new(), corners = new();
		int n = hull.Count;
		for (int i = 0; i < n; i++)
		{
			Vector2 a = hull[(i - 1 + n) % n] - hull[i],
					 b = hull[(i + 1) % n] - hull[i];
			if (Vector2.Angle(a, b) > angT) cand.Add(hull[i]);  // raw corner
		}
		foreach (var p in cand)                                // merge nearby raw corners
			if (corners.TrueForAll(c => Vector2.Distance(c, p) > distT))
				corners.Add(p);
		return corners.Count;
	}
	List<Vector2> ConvexHull(List<Vector2Int> pts)
	{
		if (pts.Count < 3) return pts.Select(p => (Vector2)p).ToList();
		var p = pts.Distinct().OrderBy(v => v.x).ThenBy(v => v.y).ToList();
		List<Vector2> l = new(), u = new();
		foreach (var t in p)
		{
			while (l.Count > 1 && Cross(l[^2], l[^1], t) <= 0) l.RemoveAt(l.Count - 1);
			l.Add(t);
		}
		for (int i = p.Count - 1; i >= 0; --i)
		{
			var t = p[i];
			while (u.Count > 1 && Cross(u[^2], u[^1], t) <= 0) u.RemoveAt(u.Count - 1);
			u.Add(t);
		}
		l.RemoveAt(l.Count - 1); u.RemoveAt(u.Count - 1); l.AddRange(u);
		return l;
	}

	private Vector3 FindCenterPoint(List<Vector2Int> absCorners, float distance)
	{
		Vector2Int center = new Vector2Int(
			(absCorners[0].x + absCorners[3].x) / 2,
			(absCorners[0].y + absCorners[3].y) / 2
		);

		return PixelToWorldPoint(center, distance);
	}
	private float GetRaycastDistance(Vector2Int texPos)
	{
		Rect r = rawImage.rectTransform.rect;
		float nx = texPos.x / (float)sketchingTool.texture.width;
		float ny = texPos.y / (float)sketchingTool.texture.height;
		Vector2 bl = RectTransformUtility.WorldToScreenPoint(null, rawImage.rectTransform.position);

		Ray ray = mainCamera.ScreenPointToRay(new Vector3(
			bl.x + Mathf.Lerp(r.xMin, r.xMax, nx),
			bl.y + Mathf.Lerp(r.yMin, r.yMax, ny),
			0f));

		if (Physics.Raycast(ray, out var hit, 100f, raycastMask))
		{
			float d = hit.distance;
			if (avoidObstruction)
			{
				float scaledOffset = visibilityOffset * (d / meshDistance);
				d -= scaledOffset;
			}
			return Mathf.Max(d, mainCamera.nearClipPlane + 0.1f);
		}
		return meshDistance;
	}

	private Vector3 PixelToWorldPoint(Vector2Int texPos, float distance)
	{
		// 1) Convert pixel coords -> local UI coords
		Rect rect = rawImage.rectTransform.rect;
		float normX = (float)texPos.x / sketchingTool.texture.width;
		float normY = (float)texPos.y / sketchingTool.texture.height;

		float localX = Mathf.Lerp(rect.xMin, rect.xMax, normX);
		float localY = Mathf.Lerp(rect.yMin, rect.yMax, normY);

		// 2) Local UI -> screen coords
		Vector2 bottomLeftScreen = RectTransformUtility.WorldToScreenPoint(null, rawImage.rectTransform.position);
		float screenX = bottomLeftScreen.x + localX;
		float screenY = bottomLeftScreen.y + localY;

		// 3) Screen -> world
		Vector3 screenPoint = new Vector3(screenX, screenY, distance);
		return mainCamera.ScreenToWorldPoint(screenPoint);
	}
	private Vector3 P2W(Vector2Int p) => PixelToWorldPoint(p, currentDistance);
	private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
	#endregion
	#endregion

	#region Shape Builders
	private Mesh BuildBoxMesh(List<Vector2Int> corners, float depth, Vector3 centerPoint)
	{
		Vector3 bl = P2W(corners[0]);
		Vector3 br = P2W(corners[1]);
		Vector3 tl = P2W(corners[2]);
		Vector3 tr = P2W(corners[3]);

		// front face
		Vector3 blF = bl;
		Vector3 brF = br;
		Vector3 tlF = tl;
		Vector3 trF = tr;

		Vector3 edge1 = brF - blF;
		Vector3 edge2 = tlF - blF;
		Vector3 normal = Vector3.Cross(edge1, edge2).normalized * depth;

		// back face
		Vector3 blB = blF + normal;
		Vector3 brB = brF + normal;
		Vector3 tlB = tlF + normal;
		Vector3 trB = trF + normal;

		blF -= centerPoint;
		brF -= centerPoint;
		tlF -= centerPoint;
		trF -= centerPoint;
		blB -= centerPoint;
		brB -= centerPoint;
		tlB -= centerPoint;
		trB -= centerPoint;

		// Indices: front= 0,1,2,3; back= 4,5,6,7
		Vector3[] verts = {
			blF, brF, tlF, trF,
			blB, brB, tlB, trB
		};

		// CCW winding for outward faces
		int[] triangles = {
            // front
            0,2,1,   2,3,1,
            // back
            4,5,6,   6,5,7,
            // bottom
            0,1,5,   0,5,4,
            // top
            2,7,3,   2,6,7,
            // left
            0,4,6,   0,6,2,
            // right
            1,3,7,   1,7,5
		};

		Vector2[] uvs = {
			new Vector2(0,0),new Vector2(1,0),
			new Vector2(0,1),new Vector2(1,1),
			new Vector2(0,0),new Vector2(1,0),
			new Vector2(0,1),new Vector2(1,1)
		};

		Mesh m = new Mesh();
		m.vertices = verts;
		m.triangles = triangles;
		m.uv = uvs;
		m.RecalculateNormals();
		m.RecalculateBounds();
		return m;
	}
	private Mesh BuildSphereMesh(List<Vector2Int> corners, Vector3 centerPoint)
	{
		Vector3 bl = P2W(corners[0]);
		Vector3 br = P2W(corners[1]);
		Vector3 tl = P2W(corners[2]);
		Vector3 tr = P2W(corners[3]);

		bl -= centerPoint;
		br -= centerPoint;
		tl -= centerPoint;
		tr -= centerPoint;

		float wBottom = Vector3.Distance(bl, br);
		float wTop = Vector3.Distance(tl, tr);
		float avgW = 0.5f * (wBottom + wTop);

		float hLeft = Vector3.Distance(bl, tl);
		float hRight = Vector3.Distance(br, tr);
		float avgH = 0.5f * (hLeft + hRight);

		float diameter = Mathf.Max(avgW, avgH);

		// bounding box center
		Vector3 bC = 0.5f * (bl + br);
		Vector3 tC = 0.5f * (tl + tr);
		Vector3 center = 0.5f * (bC + tC);

		GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		Mesh source = tmp.GetComponent<MeshFilter>().sharedMesh;
		Destroy(tmp);

		GameObject holder = new GameObject("SpherePlaceholder");
		holder.transform.position = center;
		holder.transform.localScale = Vector3.one * diameter;

		var mf = holder.AddComponent<MeshFilter>();
		mf.sharedMesh = source;

		// Bake transform
		Mesh baked = new Mesh();
		CombineInstance ci = new CombineInstance();
		ci.mesh = source;
		ci.transform = holder.transform.localToWorldMatrix;

		baked.CombineMeshes(new CombineInstance[] { ci }, true, true);
		baked.RecalculateNormals();
		baked.RecalculateBounds();

		Destroy(holder);

		return baked;
	}
	private Mesh BuildCapsuleMesh(List<Vector2Int> corners, Vector3 centerPoint)
	{
		Vector3 bl = P2W(corners[0]);
		Vector3 br = P2W(corners[1]);
		Vector3 tl = P2W(corners[2]);
		Vector3 tr = P2W(corners[3]);

		bl -= centerPoint;
		br -= centerPoint;
		tl -= centerPoint;
		tr -= centerPoint;

		float wBottom = Vector3.Distance(bl, br);
		float wTop = Vector3.Distance(tl, tr);
		float avgW = 0.5f * (wBottom + wTop);

		float hLeft = Vector3.Distance(bl, tl);
		float hRight = Vector3.Distance(br, tr);
		float avgH = 0.5f * (hLeft + hRight);

		Vector3 bC = 0.5f * (bl + br);
		Vector3 tC = 0.5f * (tl + tr);
		Vector3 center = 0.5f * (bC + tC);

		GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
		Mesh source = tmp.GetComponent<MeshFilter>().sharedMesh;
		Destroy(tmp);

		GameObject holder = new GameObject("CapsulePlaceholder");
		holder.transform.position = center;

		// default capsule is 2 tall, diameter=1
		float scaleX = avgW;
		float scaleZ = avgW;
		float scaleY = avgH / 2f;

		holder.transform.localScale = new Vector3(scaleX, scaleY, scaleZ);

		var mf = holder.AddComponent<MeshFilter>();
		mf.sharedMesh = source;

		// Bake
		Mesh baked = new Mesh();
		CombineInstance ci = new CombineInstance();
		ci.mesh = source;
		ci.transform = holder.transform.localToWorldMatrix;

		baked.CombineMeshes(new CombineInstance[] { ci }, true, true);
		baked.RecalculateNormals();
		baked.RecalculateBounds();

		Destroy(holder);

		return baked;
	}
	private Mesh BuildTriangularPrismMesh(List<Vector2Int> corners, float depth, Vector3 centerPoint)
	{
		// Ensure we have exactly 3 corners for a triangle
		if (corners.Count != 3)
		{
			Debug.LogError("A triangular mesh requires exactly 3 corners.");
			return null;
		}

		// Convert pixel coordinates to world points for front face
		Vector3 p0F = P2W(corners[0]);
		Vector3 p1F = P2W(corners[1]);
		Vector3 p2F = P2W(corners[2]);

		// Calculate normal vector for depth
		Vector3 edge1 = p1F - p0F;
		Vector3 edge2 = p2F - p0F;
		Vector3 normal = Vector3.Cross(edge1, edge2).normalized * depth;

		// Calculate back face points
		Vector3 p0B = p0F + normal;
		Vector3 p1B = p1F + normal;
		Vector3 p2B = p2F + normal;

		p0F -= centerPoint;
		p1F -= centerPoint;
		p2F -= centerPoint;
		p0B -= centerPoint;
		p1B -= centerPoint;
		p2B -= centerPoint;

		// Create vertices array - 6 vertices total (3 front, 3 back)
		// Indices: front= 0,1,2; back= 3,4,5
		Vector3[] vertices = {
		p0F, p1F, p2F,  // Front face
        p0B, p1B, p2B   // Back face
    };

		// Create triangles array with CCW winding for outward faces
		int[] triangles = {
        // Front face (1 triangle)
        0, 2, 1,
        
        // Back face (1 triangle) - reverse winding order
        3, 4, 5,
        
        // Side faces (3 quads -> 6 triangles)
        // Side 1
        0, 1, 4,
		0, 4, 3,
        
        // Side 2
        1, 2, 5,
		1, 5, 4,
        
        // Side 3
        2, 0, 3,
		2, 3, 5
	};

		// Create UVs - simplified mapping
		Vector2[] uvs = {
		new Vector2(0.5f, 0),     // Front face
        new Vector2(1, 1),
		new Vector2(0, 1),

		new Vector2(0.5f, 0),     // Back face
        new Vector2(1, 1),
		new Vector2(0, 1)
	};

		// Create and return the mesh
		Mesh mesh = new Mesh();
		mesh.vertices = vertices;
		mesh.triangles = triangles;
		mesh.uv = uvs;
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();
		return mesh;
	}
	private Mesh BuildPyramidMesh(List<Vector2Int> corners, Vector3 centerPoint)
	{
		Vector3 p0 = P2W(corners[0]);
		Vector3 p1 = P2W(corners[1]);
		Vector3 p2 = P2W(corners[2]);

		Vector3 normal = -Vector3.Cross(p1 - p0, p2 - p0).normalized;
		Vector3 center = (p0 + p1 + p2) / 3f;

		// Compute edge length (assumes roughly equilateral triangle)
		float avgLength = (Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p0)) / 3f;

		// Height of regular tetrahedron: h = sqrt(6)/3 * a
		float height = Mathf.Sqrt(6f) / 3f * avgLength;

		// Apex position directly behind triangle face
		Vector3 apex = center + normal * height;

		// Translate to local origin
		p0 -= centerPoint;
		p1 -= centerPoint;
		p2 -= centerPoint;
		apex -= centerPoint;

		Vector3[] vertices = { p0, p1, p2, apex };

		int[] triangles = {
		0, 1, 2, // base face (sketched)
		0, 3, 1,
		1, 3, 2,
		2, 3, 0
	};

		Mesh mesh = new Mesh();
		mesh.vertices = vertices;
		mesh.triangles = triangles;
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();
		return mesh;
	}
	private void AddCollider(GameObject go, Mesh m)
	{
		switch (shapeToBuild)
		{
			case ShapeType.Box:
				var bc = go.AddComponent<BoxCollider>();
				bc.center = m.bounds.center;
				bc.size = m.bounds.size;
				break;

			case ShapeType.Sphere:
				var sc = go.AddComponent<SphereCollider>();
				sc.center = m.bounds.center;
				sc.radius = Mathf.Max(m.bounds.extents.x,
									  m.bounds.extents.y,
									  m.bounds.extents.z);
				break;

			case ShapeType.Capsule:
				var cc = go.AddComponent<CapsuleCollider>();
				cc.center = m.bounds.center;
				cc.radius = Mathf.Max(m.bounds.extents.x, m.bounds.extents.z);
				cc.height = m.bounds.size.y;
				cc.direction = 1;                // Y-axis
				break;

			default:                            // TriPrism, Pyramid, etc.
				var mc = go.AddComponent<MeshCollider>();
				mc.sharedMesh = m;
				mc.convex = true;            // needed for non-static objects
				break;
		}
	}
	#endregion
}