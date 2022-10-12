using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer))]
public class MeshTrimmer:MonoBehaviour{
	
	[HideInInspector]public MeshFilter meshFilter;
	[HideInInspector]public MeshRenderer meshRenderer;
	public Material sourceMaterial;
	public Material opaqueMaterial;
	public Mesh sourceMesh;
	public Mesh trimmedMesh;
	
	private void Reset(){
		if(meshFilter==null){
			meshFilter=GetComponent<MeshFilter>();
		}
		if(meshRenderer==null){
			meshRenderer=GetComponent<MeshRenderer>();
		}
	}
}