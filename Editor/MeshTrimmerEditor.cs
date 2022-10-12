using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ClipperLib;
using LibTessDotNet.Double;
using UnityEditor;
using UnityEditor.Sprites;
using UnityEngine;

[CanEditMultipleObjects]
[CustomEditor(typeof(MeshTrimmer))]
public class MeshTrimmerEditor:Editor{
	private static int Shader_MainTex=Shader.PropertyToID("_MainTex");
	private static int Shader_Surface=Shader.PropertyToID("_Surface");
	private static int Shader_AlphaClip=Shader.PropertyToID("_AlphaClip");
	private static int Shader_Cutoff=Shader.PropertyToID("_Cutoff");
	public override void OnInspectorGUI(){
		base.DrawDefaultInspector();
		if(GUILayout.Button("TrimMesh(1) + OpaqueMaterial")){
			foreach(var obj in targets){
				var target=obj as MeshTrimmer;
				if(Trim(target,true)){
					EditorUtility.SetDirty(target);
				}
			}
		}
		if(GUILayout.Button("TrimMesh(0)")){
			foreach(var obj in targets){
				var target=obj as MeshTrimmer;
				if(Trim(target,false)){
					EditorUtility.SetDirty(target);
				}
			}
		}
		if(GUILayout.Button("Reset")){
			foreach(var obj in targets){
				var target=obj as MeshTrimmer;
				if(Reset(target)){
					EditorUtility.SetDirty(target);
				}
			}
		}
	}
	
	private class Vertex{
		public Vector3 pos;
		public Vector2 uv;
		public Vector2 uv1;
		public Vector2 uv2;
		public Color color;
		public Vector3 normal;
		public Vector4 tangent;
	}
	
	private class Plane{
		public string name;
		public int index1;
		public int index2;
		public int index3;
		public float uvA;
		public float posA;
		public List<List<IntPoint>> paths=new List<List<IntPoint>>();
	}
	
	private const double L2D=0.00000001;
	private const double D2L=100000000.0;
	private static MethodInfo SpriteUtility_GenerateOutline=null;
	
	private static Clipper clipper=new Clipper();
	private static List<List<IntPoint>> solution=new List<List<IntPoint>>();
	private static List<List<IntPoint>> texturePaths=new List<List<IntPoint>>();
	private static List<int> triangleList=new List<int>();
	private static List<Vertex> vertexList=new List<Vertex>();
	private static Dictionary<Vector3,int> vertexMap=new Dictionary<Vector3,int>();
	private static List<Plane> planeList=new List<Plane>();
	private static Dictionary<Ray,Plane> planeMap=new Dictionary<Ray,Plane>();
	
	private static Vector3[] positions;
	private static Vector2[] uvs;
	private static Vector2[] uv1s;
	private static Vector2[] uv2s;
	private static Color[] colors;
	private static Vector3[] normals;
	private static Vector4[] tangents;
	private static int[] triangles;
	
	private static bool Reset(MeshTrimmer meshTrimmer){
		if(meshTrimmer==null){
			return false;
		}
		var prefabName=meshTrimmer.name;
		var meshFilter=meshTrimmer.meshFilter;
		if(meshFilter==null){
			Debug.LogError($"{prefabName} no meshFilter");
			return false;
		}
		var meshRenderer=meshTrimmer.meshRenderer;
		if(meshRenderer==null){
			Debug.LogError($"{prefabName} no meshRenderer");
			return false;
		}
		meshTrimmer.trimmedMesh=null;
		if(meshTrimmer.sourceMesh!=null){
			meshFilter.sharedMesh=meshTrimmer.sourceMesh;
			meshTrimmer.sourceMesh=null;
		}
		meshTrimmer.opaqueMaterial=null;
		if(meshTrimmer.sourceMaterial!=null){
			meshRenderer.sharedMaterial=meshTrimmer.sourceMaterial;
			meshTrimmer.sourceMaterial=null;
		}
		return true;
	}
	
	private static bool Trim(MeshTrimmer meshTrimmer,bool isOpaque){
		if(meshTrimmer==null){
			return false;
		}
		var prefabName=meshTrimmer.name;
		var meshFilter=meshTrimmer.meshFilter;
		if(meshFilter==null){
			Debug.LogError($"{prefabName} no meshFilter");
			return false;
		}
		var meshRenderer=meshTrimmer.meshRenderer;
		if(meshRenderer==null){
			Debug.LogError($"{prefabName} no meshRenderer");
			return false;
		}
		var sourceMesh=meshTrimmer.sourceMesh;
		if(sourceMesh==null){
			sourceMesh=meshFilter.sharedMesh;
			if(sourceMesh==null){
				Debug.LogError($"{prefabName} no sharedMesh");
				return false;
			}
		}
		var meshPath=AssetDatabase.GetAssetPath(sourceMesh);
		if(string.IsNullOrEmpty(meshPath)){
			Debug.LogError($"{prefabName} sourceMesh do not exist: {meshPath}");
			return false;
		}
		var sourceMaterial=meshTrimmer.sourceMaterial;
		if(sourceMaterial==null){
			sourceMaterial=meshRenderer.sharedMaterial;
			if(sourceMaterial==null){
				Debug.LogError($"{prefabName} no sourceMaterial");
				return false;
			}
		}
		var materialPath=AssetDatabase.GetAssetPath(sourceMaterial);
		if(string.IsNullOrEmpty(materialPath)){
			Debug.LogError($"{prefabName} sourceMaterial do not exist: {materialPath}");
			return false;
		}
		if(!sourceMaterial.HasProperty(Shader_MainTex)){
			Debug.LogError($"{prefabName} sourceMaterial no property: _MainTex");
			return false;
		}
		if(!sourceMaterial.HasProperty(Shader_Surface)){
			Debug.LogError($"{prefabName} sourceMaterial no property: _Surface");
			return false;
		}
		if(!sourceMaterial.HasProperty(Shader_AlphaClip)){
			Debug.LogError($"{prefabName} sourceMaterial no property: _AlphaClip");
			return false;
		}
		if(!sourceMaterial.HasProperty(Shader_Cutoff)){
			Debug.LogError($"{prefabName} sourceMaterial no property: _Cutoff");
			return false;
		}
		var isTranparent=sourceMaterial.GetFloat(Shader_Surface)>0.5f;
		if(isTranparent){
			Debug.LogError($"{prefabName} sourceMaterial is Tranparent");
			return false;
		}
		var isAlphaClip=sourceMaterial.GetFloat(Shader_AlphaClip)>0.5f;
		if(!isAlphaClip){
			Debug.LogError($"{prefabName} sourceMaterial is Opaque");
			return false;
		}
		var mainTexture=sourceMaterial.mainTexture;
		if(mainTexture==null){
			Debug.LogError($"{prefabName} sourceMaterial no mainTexture");
			return false;
		}
		var texture=mainTexture as Texture2D;
		if(texture==null){
			Debug.LogError($"{prefabName} mainTexture is not Texture2D");
			return false;
		}
		texturePaths.Clear();
		triangleList.Clear();
		vertexList.Clear();
		vertexMap.Clear();
		planeList.Clear();
		planeMap.Clear();
		
		positions=sourceMesh.vertices;
		uvs=sourceMesh.uv;
		uv1s=sourceMesh.uv2;
		uv2s=sourceMesh.uv3;
		colors=sourceMesh.colors;
		normals=sourceMesh.normals;
		tangents=sourceMesh.tangents;
		triangles=sourceMesh.triangles;
		
		var triangleCount=triangles.Length;
		if(triangleCount<3){
			Debug.LogError($"{prefabName} empty sourceMesh: {meshPath}");
			return false;
		}
		if(triangleCount>300){
			Debug.LogError($"{prefabName} sourceMesh more than 100 triangles: {meshPath}");
			return false;
		}
		RoundPositionsAndUvs();
		var cutoff=sourceMaterial.GetFloat(Shader_Cutoff);
		var alphaTolerance=(byte)Mathf.Max(Mathf.RoundToInt(cutoff*255f),1);
		GenerateTexturePaths(texture,isOpaque?1f:0f,alphaTolerance,true);
		if(!GeneratePlaneList(prefabName)){
			return false;
		}
		foreach(var plane in planeList){
			if(!TriangulatePlane(plane)){
				return false;
			}
		}
		triangleCount=triangleList.Count;
		if(triangleCount<3){
			Debug.LogError($"{prefabName} empty trimmedMesh: {meshPath}");
			return false;
		}
		if(isOpaque){
			meshTrimmer.sourceMaterial=sourceMaterial;
			meshTrimmer.opaqueMaterial=new Material(sourceMaterial);
			meshRenderer.sharedMaterial=meshTrimmer.opaqueMaterial;
			var materialFileName="Opaque_"+prefabName+"_prefab.mat";
			CreateOpaqueMaterial(meshTrimmer.opaqueMaterial,GetGeneratePath(materialPath,materialFileName));
		}
		else{
			meshTrimmer.opaqueMaterial=null;
			if(meshTrimmer.sourceMaterial!=null){
				meshRenderer.sharedMaterial=meshTrimmer.sourceMaterial;
				meshTrimmer.sourceMaterial=null;
			}
		}
		meshTrimmer.sourceMesh=sourceMesh;
		meshTrimmer.trimmedMesh=new Mesh();
		meshFilter.sharedMesh=meshTrimmer.trimmedMesh;
		var meshFileName="Trimmed_"+prefabName+"_prefab.asset";
		CreateTrimmedMesh(meshTrimmer.trimmedMesh,GetGeneratePath(meshPath,meshFileName));
		AssetDatabase.SaveAssets();
		if(triangleCount>3000){
			Debug.LogWarning($"{prefabName} trimmedMesh triangles: {triangleCount}");
		}
		else{
			Debug.Log($"{prefabName} trimmedMesh triangles: {triangleCount}");
		}
		return true;
	}
	
	private static string GetGeneratePath(string sourcePath,string fileName){
		var sourceFolder=Path.GetDirectoryName(sourcePath);
		var folder=Path.Combine(sourceFolder,"Generate");
		if(!AssetDatabase.IsValidFolder(folder)){
			AssetDatabase.CreateFolder(sourceFolder,"Generate");
		}
		return Path.Combine(folder,fileName);
	}
	
	private static void RoundPositionsAndUvs(){
		for(int i=0;i<positions.Length;i++){
			positions[i].x=(float)Math.Round(positions[i].x,5);
			positions[i].y=(float)Math.Round(positions[i].y,5);
			positions[i].z=(float)Math.Round(positions[i].z,5);
		}
		for(int i=0;i<uvs.Length;i++){
			uvs[i].x=(float)Math.Round(uvs[i].x,7);
			uvs[i].y=(float)Math.Round(uvs[i].y,7);
		}
	}
	
	private static float Cross(Vector2 lhs,Vector2 rhs){
		return (float)((double)lhs.x*(double)rhs.y-(double)rhs.x*(double)lhs.y);
	}
	
	private static bool GeneratePlaneList(string objectName){
		var intUvs=ConvertToIntPoints(uvs);
		var triangleCount=triangles.Length/3;
		for(int t=0;t<triangleCount;t++){
			var index1=triangles[t*3];
			var index2=triangles[t*3+1];
			var index3=triangles[t*3+2];
			var uv1=uvs[index1];
			var uv2=uvs[index2];
			var uv3=uvs[index3];
			var uvA=Cross(uv1-uv3,uv1-uv2);
			if(uvA==0){
				Debug.LogError($"{objectName} UV Cross Failed: triangle{t}");
				return false;
			}
			var pos1=positions[index1];
			var pos2=positions[index2];
			var pos3=positions[index3];
			var posC=Vector3.Cross(pos1-pos3,pos1-pos2);
			var posA=posC.magnitude;
			if(posA==0){
				Debug.LogError($"{objectName} Vertex Cross Failed: triangle{t}");
				return false;
			}
			
			var normal=posC.normalized;
			var distance=Vector3.Dot(normal,pos1);
			var key=new Ray(new Vector3(uvA,posA,distance),normal);
			if(!planeMap.TryGetValue(key,out var plane)){
				plane=new Plane();
				plane.name=$"{objectName}_triangle{t}_plane{planeList.Count}";
				plane.index1=index1;
				plane.index2=index2;
				plane.index3=index3;
				plane.uvA=uvA;
				plane.posA=posA;
				planeList.Add(plane);
				planeMap.Add(key,plane);
			}
			else{
				Debug.Log($"{t} {uvA} {posA}");
			}
			plane.paths.Add(new List<IntPoint>(3){intUvs[index1],intUvs[index2],intUvs[index3]});
		}
		return true;
	}
	
	private static int GetUvRepeat(float uv){
		return (uv<0?Mathf.FloorToInt(uv):(Mathf.CeilToInt(uv)-1));
	}
	
	private static void GenerateTexturePaths(Texture2D texture,float detail,byte alphaTolerance,bool holeDetection){
		var xMin=float.MaxValue;
		var yMin=float.MaxValue;
		var xMax=float.MinValue;
		var yMax=float.MinValue;
		foreach(var uv in uvs){
			if(xMin>uv.x){
				xMin=uv.x;
			}
			if(yMin>uv.y){
				yMin=uv.y;
			}
			if(xMax<uv.x){
				xMax=uv.x;
			}
			if(yMax<uv.x){
				yMax=uv.x;
			}
		}
		var xMinRepeat=GetUvRepeat(xMin);
		var yMinRepeat=GetUvRepeat(yMin);
		var xMaxRepeat=GetUvRepeat(xMax);
		var yMaxRepeat=GetUvRepeat(yMax);
		var textureWidth=texture.width;
		var textureHeight=texture.height;
		var textureRect=new Rect(0,0,textureWidth,textureHeight);
		if(SpriteUtility_GenerateOutline==null){
			var type=typeof(SpriteUtility);
			var attr=BindingFlags.NonPublic|BindingFlags.Static;
			SpriteUtility_GenerateOutline=type.GetMethod("GenerateOutline",attr);
		}
		var parameters=new object[]{texture,textureRect,detail,alphaTolerance,holeDetection,null};
		SpriteUtility_GenerateOutline.Invoke(null,parameters);
		var textureOutline=(Vector2[][])parameters[parameters.Length-1];
		for(var y=yMinRepeat;y<=yMaxRepeat;y++){
			for(var x=xMinRepeat;x<=xMaxRepeat;x++){
				var offsetWidth=textureWidth*x+textureWidth/2;
				var offsetHeight=textureHeight*y+textureHeight/2;
				var outlinePathCount=textureOutline.Length;
				for(int i=0;i<outlinePathCount;i++){
					var outlinePath=textureOutline[i];
					var pointCount=outlinePath.Length;
					var path=new List<IntPoint>(pointCount);
					for(int j=0;j<pointCount;j++){
						var point=outlinePath[j];
						var intPointX=Math.Round(D2L*(point.x+offsetWidth)/textureWidth);
						var intPointY=Math.Round(D2L*(point.y+offsetHeight)/textureHeight);
						path.Add(new IntPoint(intPointX,intPointY));
					}
					texturePaths.Add(path);
				}
			}
		}
	}
	
	private static IntPoint[] ConvertToIntPoints(Vector2[] points){
		var pointCount=points.Length;
		var path=new IntPoint[pointCount];
		for(int i=0;i<pointCount;i++){
			var point=points[i];
			var x=Math.Round(D2L*point.x);
			var y=Math.Round(D2L*point.y);
			path[i]=new IntPoint(x,y);
		}
		return path;
	}
	
	private static bool TriangulatePlane(Plane plane){
		if(plane.paths.Count<=0){
			return true;
		}
		solution.Clear();
		clipper.Clear();
		clipper.ReverseSolution=plane.uvA>0;
		clipper.AddPaths(plane.paths,PolyType.ptSubject,true);
		clipper.AddPaths(texturePaths,PolyType.ptClip,true);
		if(!clipper.Execute(ClipType.ctIntersection,solution,PolyFillType.pftNonZero)){
			Debug.LogError($"{plane.name} Clipper.Execute Failed ");
			return false;
		}
		var pathCount=solution.Count;
		if(pathCount<=0){
			return true;
		}
		var tess=new Tess();
		for(int i=0;i<pathCount;i++){
			var intPath=solution[i];
			var pointCount=intPath.Count;
			var path=new ContourVertex[pointCount];
			for(int j=0;j<pointCount;j++){
				var intPoint=intPath[j];
				var vertex=CreateVertexByUv(plane,new Vector2((float)(L2D*intPoint.X),(float)(L2D*intPoint.Y)));
				path[j]=new ContourVertex(new Vec3(vertex.pos.x,vertex.pos.y,vertex.pos.z),vertex);
			}
			tess.AddContour(path);
		}
		tess.Tessellate();
		var tessVertices=tess.Vertices;
		var tessElements=tess.Elements;
		if(tess.VertexCount<=0||tess.ElementCount<=0){
			Debug.LogError($"{plane.name} Triangulate Failed");
			return false;
		}
		foreach(var tessElement in tessElements){
			var tessVertex=tessVertices[tessElement];
			var tessPos=tessVertex.Position;
			var pos=new Vector3((float)tessPos.X,(float)tessPos.Y,(float)tessPos.Z);
			if(!vertexMap.TryGetValue(pos,out var vertexIndex)){
				var vertex=tessVertex.Data as Vertex;
				if(vertex==null){
					vertex=CreateVertexByPos(plane,pos);
				}
				vertexIndex=vertexList.Count;
				vertexMap.Add(pos,vertexIndex);
				vertexList.Add(vertex);
			}
			triangleList.Add(vertexIndex);
		}
		return true;
	}
	
	private static Vertex CreateVertexByUv(Plane plane,Vector2 uv){
		var vertex=new Vertex();
		var d1=uvs[plane.index1]-uv;
		var d2=uvs[plane.index2]-uv;
		var d3=uvs[plane.index3]-uv;
		var a1=(Cross(d3,d2)/plane.uvA);
		var a2=(Cross(d1,d3)/plane.uvA);
		var a3=(Cross(d2,d1)/plane.uvA);//1-a1-a2;
		vertex.pos=(positions[plane.index1]*a1)+(positions[plane.index2]*a2)+(positions[plane.index3]*a3);
		vertex.uv=uv;
		InitVertex(vertex,plane,a1,a2,a3);
		return vertex;
	}
	
	private static Vertex CreateVertexByPos(Plane plane,Vector3 pos){
		var vertex=new Vertex();
		var d1=positions[plane.index1]-pos;
		var d2=positions[plane.index2]-pos;
		var d3=positions[plane.index3]-pos;
		var a1=(Vector3.Cross(d3,d2).magnitude/plane.posA);
		var a2=(Vector3.Cross(d1,d3).magnitude/plane.posA);
		var a3=(Vector3.Cross(d2,d1).magnitude/plane.posA);//1-a1-a2;
		vertex.pos=pos;
		vertex.uv=(uvs[plane.index1]*a1)+(uvs[plane.index2]*a2)+(uvs[plane.index3]*a3);
		InitVertex(vertex,plane,a1,a2,a3);
		return vertex;
	}
	
	private static void InitVertex(Vertex vertex,Plane plane,float a1,float a2,float a3){
		if(uv1s!=null&&uv1s.Length>0){
			vertex.uv1=(uv1s[plane.index1]*a1)+(uv1s[plane.index2]*a2)+(uv1s[plane.index3]*a3);
		}
		if(uv2s!=null&&uv2s.Length>0){
			vertex.uv2=(uv2s[plane.index1]*a1)+(uv2s[plane.index2]*a2)+(uv2s[plane.index3]*a3);
		}
		if(colors!=null&&colors.Length>0){
			vertex.color=(colors[plane.index1]*a1)+(colors[plane.index2]*a2)+(colors[plane.index3]*a3);
		}
		if(normals!=null&&normals.Length>0){
			vertex.normal=(normals[plane.index1]*a1)+(normals[plane.index2]*a2)+(normals[plane.index3]*a3);
		}
		if(tangents!=null&&tangents.Length>0){
			vertex.tangent=(tangents[plane.index1]*a1)+(tangents[plane.index2]*a2)+(tangents[plane.index3]*a3);
		}
	}
	
	private static void CreateOpaqueMaterial(Material material,string path){
		material.SetFloat(Shader_Surface,0f);
		material.SetFloat(Shader_AlphaClip,0f);
		BaseShaderGUI.SetupMaterialBlendMode(material);
		AssetDatabase.CreateAsset(material,path);
	}
	
	private static void CreateTrimmedMesh(Mesh mesh,string path){
		var newVertexCount=vertexList.Count;
		var newVertices=new Vector3[newVertexCount];
		var newUvs=new Vector2[newVertexCount];
		for(int i=0;i<newVertexCount;i++){
			var vertex=vertexList[i];
			newVertices[i]=vertex.pos;
			newUvs[i]=vertex.uv;
		}
		mesh.vertices=newVertices;
		mesh.uv=newUvs;
		if(uv1s!=null&&uv1s.Length>0){
			var uv1s=new Vector2[newVertexCount];
			for(int i=0;i<newVertexCount;i++){
				uv1s[i]=vertexList[i].uv1;
			}
			mesh.uv2=uv1s;
		}
		if(uv2s!=null&&uv2s.Length>0){
			var uv2s=new Vector2[newVertexCount];
			for(int i=0;i<newVertexCount;i++){
				uv2s[i]=vertexList[i].uv2;
			}
			mesh.uv3=uv2s;
		}
		if(colors!=null&&colors.Length>0){
			var colors=new Color[newVertexCount];
			for(int i=0;i<newVertexCount;i++){
				colors[i]=vertexList[i].color;
			}
			mesh.colors=colors;
		}
		if(normals!=null&&normals.Length>0){
			var newNormals=new Vector3[newVertexCount];
			for(int i=0;i<newVertexCount;i++){
				newNormals[i]=vertexList[i].normal;
			}
			mesh.normals=newNormals;
		}
		if(tangents!=null&&tangents.Length>0){
			var newTangents=new Vector4[newVertexCount];
			for(int i=0;i<newVertexCount;i++){
				newTangents[i]=vertexList[i].tangent;
			}
			mesh.tangents=newTangents;
		}
		mesh.triangles=triangleList.ToArray();
		AssetDatabase.CreateAsset(mesh,path);
	}
}