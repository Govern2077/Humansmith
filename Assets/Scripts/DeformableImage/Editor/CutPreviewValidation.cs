#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
namespace DeformableImage.Editor
{
 public static class CutPreviewValidation
 {
  const BindingFlags P=BindingFlags.Instance|BindingFlags.NonPublic;
  [MenuItem("Tools/Face/Validate Transparent Cut Preview")]
  public static void RunBatch()
  {
   GameObject go=null;Texture2D tex=null;Sprite sprite=null;DeformableImageMesh mesh=null;
   try
   {
    tex=new Texture2D(1024,1024,TextureFormat.RGBA32,false);var pixels=new Color32[1024*1024];for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(255,255,255,255);tex.SetPixels32(pixels);tex.Apply();sprite=Sprite.Create(tex,new Rect(0,0,1024,1024),Vector2.one*.5f);
    go=new GameObject("Transparent preview fixture"){hideFlags=HideFlags.HideAndDontSave};mesh=go.AddComponent<DeformableImageMesh>();Set(mesh,"subdivisionsX",64);Set(mesh,"subdivisionsY",64);mesh.ConfigureSprites(sprite,sprite);
    var tool=go.AddComponent<DeformableImageInteractor>();Call(tool,"Awake");Set(tool,"transparentCutWidth",.08f);int rebuilds=0;mesh.MeshRebuilt+=()=>rebuilds++;
    var before=mesh.CopyCurrentVertices();var triangles=mesh.CopyLogicalTriangles();var alpha=(byte[])Get<byte[]>(mesh,"cutMaskAlpha").Clone();Start(tool,mesh);Add(tool,.2f,.5f);
    double total=0,max=0;var watch=new Stopwatch();for(int i=1;i<=64;i++){watch.Restart();Add(tool,.2f+.6f*i/64,.5f);watch.Stop();double ms=watch.Elapsed.TotalMilliseconds;total+=ms;max=Math.Max(max,ms);}
    Require(mesh.HasCutPreview&&rebuilds==0,"Preview does not rebuild mesh");Same(before,mesh.CopyCurrentVertices());Same(triangles,mesh.CopyLogicalTriangles());Same(alpha,Get<byte[]>(mesh,"cutMaskAlpha"));Require(!Get<bool>(mesh,"hasAppliedCuts"),"Preview has no mechanical incision");
    Require(mesh.IsVisibleMaterialAtCurrentPoint(Local(.3f,.5f))&&mesh.TryGetNormalizedUvAtCurrentPoint(Local(.3f,.5f),out _),"Preview remains pickable");var preview=Get<Texture2D>(mesh,"cutPreviewTexture");Require(preview.GetPixelBilinear(.3f,.5f).r<.1f,"Transparent trail");var props=new MaterialPropertyBlock();var renderer=mesh.GetComponent<MeshRenderer>();for(int side=0;side<2;side++){renderer.GetPropertyBlock(props,side);Require(props.GetTexture("_CutMask")==preview,"Front/back render preview");}
    Call(tool,"CancelCut");Require(!mesh.HasCutPreview&&rebuilds==0,"Cancel restores preview");Same(alpha,Get<byte[]>(mesh,"cutMaskAlpha"));renderer.GetPropertyBlock(props,0);Require(props.GetTexture("_CutMask")==Get<Texture2D>(mesh,"cutMaskTexture"),"Cancel restores material");
    Start(tool,mesh);Add(tool,.25f,.7f);Add(tool,.4f,.7f);Require(Get<Texture2D>(mesh,"cutPreviewTexture").GetPixelBilinear(.3f,.5f).r>.9f,"No stale cancelled trail");watch.Restart();Call(tool,"CompleteCut");watch.Stop();double release=watch.Elapsed.TotalMilliseconds;Require(rebuilds==1&&!mesh.HasCutPreview&&!mesh.IsVisibleMaterialAtCurrentPoint(Local(.3f,.7f)),"One physical cut on release");
    Start(tool,mesh);Add(tool,.25f,.8f);Add(tool,.4f,.8f);Require(Get<Texture2D>(mesh,"cutPreviewTexture").GetPixelBilinear(.3f,.7f).r<.1f,"Previous real cut stays visible");tool.ActiveTool=DeformableImageInteractor.DeformationTool.Inflate;Require(!mesh.HasCutPreview&&mesh.IsVisibleMaterialAtCurrentPoint(Local(.3f,.8f))&&!mesh.IsVisibleMaterialAtCurrentPoint(Local(.3f,.7f)),"Tool switch cancels only preview");
    Start(tool,mesh);Add(tool,.25f,.8f);Add(tool,.4f,.8f);Call(tool,"OnDisable");Require(!mesh.HasCutPreview&&rebuilds==1,"Disable cancels preview");
    Start(tool,mesh);Add(tool,.25f,.25f);Add(tool,.45f,.25f);Add(tool,.45f,.4f);Add(tool,.25f,.4f);Add(tool,.25f,.25f);Require(rebuilds==1,"Closed loop waits for release");Call(tool,"CompleteCut");bool piece=false;foreach(var item in Resources.FindObjectsOfTypeAll<DeformableImageMesh>())if(item.DetachedFromMesh==mesh)piece=true;Require(piece&&!mesh.IsVisibleMaterialAtCurrentPoint(Local(.35f,.32f)),"Closed piece on release");
    File.WriteAllText("CutPreviewPerformance.txt","Synthetic 1024x1024 texture, 64x64 grid. Headless CPU; excludes GPU, scene rendering and layer rebind. 64 preview updates: total="+total.ToString("F2")+" ms, mean="+(total/64).ToString("F2")+" ms, max="+max.ToString("F2")+" ms. Short-path release="+release.ToString("F2")+" ms.");
    File.WriteAllText("CutPreviewValidation.txt","PASS: transparent front/back mask; no vertex/triangle/persistent mask/mechanical incision change while drawing; picking retained; cancel/tool switch/disable restore preview; no stale trail; previous real cuts retained; one physical open-cut rebuild on release; closed fragment on release.");
   }
   catch(Exception e){File.WriteAllText("CutPreviewValidation.txt","FAIL: "+e);UnityEngine.Debug.LogException(e);EditorApplication.Exit(1);return;}
   finally{if(mesh!=null)foreach(var item in Resources.FindObjectsOfTypeAll<DeformableImageMesh>())if(item!=null&&item.DetachedFromMesh==mesh)UnityEngine.Object.DestroyImmediate(item.gameObject);if(go!=null)UnityEngine.Object.DestroyImmediate(go);if(sprite!=null)UnityEngine.Object.DestroyImmediate(sprite);if(tex!=null)UnityEngine.Object.DestroyImmediate(tex);}
   ManualDragValidation.RunBatch();
  }
  static Vector3 Local(float x,float y){return new Vector3((x-.5f)*5,(y-.5f)*5,0);}
  static void Start(DeformableImageInteractor t,DeformableImageMesh m){Call(t,"ClearCutStroke");Set(t,"activeTool",DeformableImageInteractor.DeformationTool.Cut);Set(t,"activeCutMesh",m);Set(t,"cutActive",true);}
  static void Add(DeformableImageInteractor t,float x,float y){Get<List<Vector2>>(t,"cutPathUvs").Add(new Vector2(x,y));Get<List<Vector3>>(t,"cutPathWorld").Add(Local(x,y));Call(t,"UpdateCutMaskPreview");}
  static T Get<T>(object o,string n){return (T)o.GetType().GetField(n,P).GetValue(o);}
  static void Set(object o,string n,object v){o.GetType().GetField(n,P).SetValue(o,v);}
  static void Call(object o,string n){o.GetType().GetMethod(n,P).Invoke(o,null);}
  static void Require(bool b,string n){if(!b)throw new Exception(n);}
  static void Same<T>(T[] a,T[] b){Require(a.Length==b.Length,"Array length unchanged");for(int i=0;i<a.Length;i++)Require(EqualityComparer<T>.Default.Equals(a[i],b[i]),"Array contents unchanged");}
 }
}
#endif