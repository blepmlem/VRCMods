using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Harmony;
using LagFreeScreenshots;
using MelonLoader;
using UIExpansionKit.API;
using UnhollowerRuntimeLib.XrefScans;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.UserCamera;
using VRC.Core;
using VRC;
using Object = UnityEngine.Object;
using CameraTakePhotoEnumerator = VRC.UserCamera.CameraUtil.ObjectNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObBoAcIn2StInTeCaUnique;
using System.Collections.Generic;
using System.Globalization;

// using CameraUtil = ObjectPublicCaSiVeUnique;

[assembly:MelonInfo(typeof(LagFreeScreenshotsMod), "Lag Free Screenshots", "1.2.1", "knah, Protected", "https://github.com/knah/VRCMods")]
[assembly:MelonGame("VRChat", "VRChat")]
[assembly:MelonOptionalDependencies("UIExpansionKit")]

namespace LagFreeScreenshots
{
    public class LagFreeScreenshotsMod : MelonMod
    {
        private class Metadata
        {
            private const int ArtistExif = 0x013B;
            public string Author
            {
                get => _data.TryGetValue(nameof(Author), out var value) ? value : string.Empty;
                set => _data[nameof(Author)] = value;
            }
            
            public string AuthorId
            {
                get => _data.TryGetValue(nameof(AuthorId), out var value) ? value : string.Empty;
                set => _data[nameof(AuthorId)] = value;
            }

            public string World
            {
                get => _data.TryGetValue(nameof(World), out var value) ? value : string.Empty;
                set => _data[nameof(World)] = value;
            }
            
            public string WorldId
            {
                get => _data.TryGetValue(nameof(WorldId), out var value) ? value : string.Empty;
                set => _data[nameof(WorldId)] = value;
            }
            
            public string Position
            {
                get => _data.TryGetValue(nameof(Position), out var value) ? value : string.Empty;
                set => _data[nameof(Position)] = value;
            }

            public string Players
            {
                get => _data.TryGetValue(nameof(Players), out var value) ? value : string.Empty;
                set => _data[nameof(Players)] = value;
            }
            
            public string PlayerIds
            {
                get => _data.TryGetValue(nameof(PlayerIds), out var value) ? value : string.Empty;
                set => _data[nameof(PlayerIds)] = value;
            }
            
            private Dictionary<string, string> _data = new Dictionary<string, string>();

            public string Set(string keyword, string value) => _data[keyword] = value;
            public IEnumerable<(string keyword, string value)> Get() => _data.Select(d => (d.Key, d.Value));
        }
        
        private static readonly AwaitProvider ourToMainThread = new AwaitProvider();
        private static readonly AwaitProvider ourToEndOfFrame = new AwaitProvider();

        private const string SettingsCategory = "LagFreeScreenshots";
        private const string SettingEnableMod = "Enabled";
        private const string SettingScreenshotFormat = "ScreenshotFormat";
        private const string SettingAutorotation = "Auto-rotation";
        private const string SettingFileNamePlayers = "FileNamePlayers";

        private static MelonPreferences_Entry<bool> ourEnabled;
        private static MelonPreferences_Entry<bool> ourAutorotation;
        private static MelonPreferences_Entry<bool> ourSaveFileNamePlayers;

        private static Thread ourMainThread;

        public override void OnApplicationStart()
        {
            var category = MelonPreferences.CreateCategory(SettingsCategory, "Lag Free Screenshots");
            ourEnabled = (MelonPreferences_Entry<bool>) category.CreateEntry(SettingEnableMod, true, "Enabled");
            ourAutorotation = (MelonPreferences_Entry<bool>)category.CreateEntry(SettingAutorotation, true, "Rotate picture to match camera");
            ourSaveFileNamePlayers = (MelonPreferences_Entry<bool>)category.CreateEntry(SettingFileNamePlayers, false, "Replace filename with names of players in view");

            Harmony.Patch(
                typeof(CameraTakePhotoEnumerator).GetMethod("MoveNext"),
                new HarmonyMethod(AccessTools.Method(typeof(LagFreeScreenshotsMod), nameof(MoveNextPatchAsyncReadback))));
            
            if (MelonHandler.Mods.Any(it => it.Info.Name == "UI Expansion Kit" && !it.Info.Version.StartsWith("0.1."))) 
                AddEnumSettings();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddEnumSettings()
        {
            ExpansionKitApi.RegisterSettingAsStringEnum(SettingsCategory, SettingScreenshotFormat, new []{("png", "PNG"), ("jpeg", "JPEG")});
        }

        private static int GetPictureAutorotation(Camera camera)
        {
            var pitch = Vector3.Angle(camera.transform.forward, new Vector3(0, 1, 0));
            if (pitch < 45 || pitch > 135) return 0; //Pointing up/down, rotation doesn't matter

            var rot = camera.transform.localEulerAngles.z;
            if (rot >= 45 && rot < 135) return 3;
            if (rot >= 135 && rot < 225) return 2;
            if (rot >= 225 && rot < 315) return 1;
            return 0;
        }

        private static (string playerNames, string playerIds)? GetPlayerList(Camera camera, string separator)
        {
            var playerManager = PlayerManager.field_Private_Static_PlayerManager_0;

            if (playerManager == null) return null;

            var result = new List<(string name, string id, float zDistance)>();


            var localPlayer = VRCPlayer.field_Internal_Static_VRCPlayer_0;
            var localPosition = localPlayer.gameObject.transform.position;

            foreach (var p in playerManager.field_Private_List_1_Player_0)
            {
                var avatarRoot = p.prop_VRCPlayer_0.prop_VRCAvatarManager_0.transform.Find("Avatar");
                
                Animator animator;
                Transform playerPositionTransform;
                if (avatarRoot != null && (animator = avatarRoot.GetComponent<Animator>()) != null)
                {
                    playerPositionTransform = animator.GetBoneTransform(HumanBodyBones.Head);
                }
                else
                {
                    playerPositionTransform = p.transform;
                }
                
                var playerPosition = playerPositionTransform.position;
                Vector3 viewPos = camera.WorldToViewportPoint(playerPosition);
                var playerDescriptor = p.prop_APIUser_0.displayName;
                
                if (string.Equals(playerDescriptor, APIUser.CurrentUser.displayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool shouldAdd = false;
                if (viewPos.z < 2 && Vector3.Distance(localPosition, playerPosition) < 2)
                {
                    //User standing right next to photographer, might be visible (approx.)
                    shouldAdd = true;
                }
                else if (viewPos.x > -0.03 && viewPos.x < 1.03 && viewPos.y > -0.03 && viewPos.y < 1.03 && viewPos.z > 2 && viewPos.z < 30)
                {
                    //User in viewport, might be obstructed but still...
                    shouldAdd = true;
                }

                if (shouldAdd)
                {
                    result.Add((playerDescriptor.Replace(separator, ""), p.prop_APIUser_0.id, viewPos.z));
                }
            }

            if (!result.Any())
            {
                return null;
            }

            result = result.OrderBy(x => x.zDistance).ToList();
            return (string.Join(separator, result.Select(x => x.name)), string.Join(separator, result.Select(x => x.id)));
        }

        private static string GetPhotographerMeta() => APIUser.CurrentUser.displayName;

        private static (string worldName, string worldId)? GetWorldMeta()
        {
            var apiWorld = RoomManager.field_Internal_Static_ApiWorld_0;
            if (apiWorld == null) return null;
            return (apiWorld.name, apiWorld.id);
        }

        private static string GetPosition()
        {
            var position = VRCPlayer.field_Internal_Static_VRCPlayer_0.transform.position;
            return position.x.ToString(CultureInfo.InvariantCulture) + "," +
                   position.y.ToString(CultureInfo.InvariantCulture) + "," +
                   position.z.ToString(CultureInfo.InvariantCulture);
        }

        public override void OnUpdate()
        {
            ourToMainThread.Flush();
        }

        public override void OnGUI()
        {
            ourToEndOfFrame.Flush();
        }

        public static bool MoveNextPatchAsyncReadback(ref bool __result, CameraTakePhotoEnumerator __instance)
        {
            var resX = __instance.field_Public_Int32_0;
            var resY = __instance.field_Public_Int32_1;
            var saveToFile = __instance.field_Public_Boolean_0;
            var hasAlpha = __instance.field_Public_Boolean_1;
            
            MelonDebug.Msg($"LFS bools: 0={__instance.field_Public_Boolean_0} 1={__instance.field_Public_Boolean_1}");
            
            if (!ourEnabled.Value || !saveToFile)
                return true;
            
            ourMainThread = Thread.CurrentThread;

            __result = false;
            TakeScreenshot(__instance.field_Public_Camera_0, resX,
                resY, hasAlpha).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    MelonLogger.Warning($"Free-floating task failed with exception: {t.Exception}");
            });
            return false;
        }

        private static int MaxMsaaCount(int w, int h)
        {
            // MSAA rendertargets store depth (24+8 bits?) and color per sample, plus one extra color sample (output color?) for levels >1
            // Unity doesn't like rendertextures over 4 gigs in size, so reduce MSAA if necessary
            var colorSizePerLevel = w * (long) h * 4 * 2; // ignore no-alpha to be conservative about packing
            var maxMsaa = (uint.MaxValue - colorSizePerLevel / 2) / colorSizePerLevel;
            if (maxMsaa >= 8) return 8;
            if (maxMsaa >= 4) return 4;
            if (maxMsaa >= 2) return 2;
            return 1;
        }
        
        public static async Task TakeScreenshot(Camera camera, int w, int h, bool hasAlpha)
        {
            await ourToEndOfFrame.Yield();

            // var renderTexture = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 8);
            var renderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            renderTexture.antiAliasing = MaxMsaaCount(w, h);

            var oldCameraTarget = camera.targetTexture;
            var oldCameraFov = camera.fieldOfView;

            camera.targetTexture = renderTexture;
            
            camera.Render();

            camera.targetTexture = oldCameraTarget;
            camera.fieldOfView = oldCameraFov;

            (IntPtr, int) data = default;
            var readbackSupported = SystemInfo.supportsAsyncGPUReadback;
            if (readbackSupported)
            {
                MelonDebug.Msg("Supports readback");
                
                var stopwatch = Stopwatch.StartNew();
                var request = AsyncGPUReadback.Request(renderTexture, 0, hasAlpha ? TextureFormat.ARGB32 : TextureFormat.RGB24, new Action<AsyncGPUReadbackRequest>(r =>
                {
                    if (r.hasError)
                        MelonLogger.Warning("Readback request finished with error (w)");
                    
                    data = ToBytes(r.GetDataRaw(0), r.GetLayerDataSize());
                    MelonDebug.Msg($"Bytes readback took total {stopwatch.ElapsedMilliseconds}");
                }));
                
                while (!request.done && !request.hasError && data.Item1 == IntPtr.Zero)
                    await ourToMainThread.Yield();

                if (request.hasError)
                    MelonLogger.Warning("Readback request finished with error");
                
                if (data.Item1 == IntPtr.Zero)
                {
                    MelonDebug.Msg("Data was null after request was done, waiting more");
                    await ourToMainThread.Yield();
                }
            }
            else
            {
                MelonLogger.Msg("Does not support readback, using fallback texture read method");
                
                RenderTexture.active = renderTexture;
                var newTexture = new Texture2D(w, h, hasAlpha ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
                newTexture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                newTexture.Apply();
                RenderTexture.active = null;

                var bytes = newTexture.GetRawTextureData();
                data = (Marshal.AllocHGlobal(bytes.Length), bytes.Length);
                Il2CppSystem.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Item1, bytes.Length);
                
                Object.Destroy(newTexture);
            }
            
            Object.Destroy(renderTexture);

            var targetFile = GetPath(w, h);
            var targetDir = Path.GetDirectoryName(targetFile);
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            string metadataStr = null;
            int rotationQuarters = 0;
            
            if (ourAutorotation.Value) 
                rotationQuarters = GetPictureAutorotation(camera);

            string separator = ";";
            
            var author = GetPhotographerMeta();
            var world = GetWorldMeta();
            var pos = GetPosition();
            var players = GetPlayerList(camera, separator);
            
            var metadata = new Metadata
            {
                Author = author,
                Position = pos,
            };

            if (world.HasValue)
            {
                metadata.World = world.Value.worldName;
                metadata.WorldId = world.Value.worldId;
            }
            
            if (players.HasValue)
            {
                metadata.Players = players.Value.playerNames;
                metadata.PlayerIds = players.Value.playerIds;
                
                if (ourSaveFileNamePlayers.Value)
                {
                    var names = metadata.Players.Replace(separator, "] [");
                    var invalids = Path.GetInvalidFileNameChars();
                    var sanitizedNames = String.Join("_", names.Split(invalids, StringSplitOptions.RemoveEmptyEntries) );
                    try
                    {
                        var extension = ".png";
                        var dir = Path.GetDirectoryName(targetFile);
                        var characters = dir!.Replace(@"\", @"\\").Length + extension.Length + 4;

                        const int MAX_PATH = 255;
                        string final = (characters + sanitizedNames.Length > MAX_PATH) ? sanitizedNames.Substring(0, MAX_PATH - characters) : sanitizedNames;
                        targetFile = Path.Combine(dir ,$"[{final}]{extension}");
                    }
                    catch (PathTooLongException e)
                    {
                        MelonLogger.Error($"File path was too long - it's a bug!\n{e}");
                    }
                }
            }

            await EncodeAndSavePicture(targetFile, data, w, h, hasAlpha, rotationQuarters, metadata)
                .ConfigureAwait(false);
        }
        
        private static unsafe (IntPtr, int) ToBytes(IntPtr pointer, int length)
        {
            var data = Marshal.AllocHGlobal(length);
            
            Buffer.MemoryCopy((void*) pointer, (void*) data, length, length);

            return (data, length);
        }
        
        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }

            return null;
        }

        private static unsafe (IntPtr, int) TransposeAndDestroyOriginal((IntPtr, int Length) data, int w, int h, int step)
        {
            (IntPtr, int) newData = (Marshal.AllocHGlobal(data.Length), data.Length);

            byte* pixels = (byte*) data.Item1;
            byte* newPixels = (byte*) newData.Item1;
            for (var x = 0; x < w; x++)
            for (var y = 0; y < h; y++)
            for (var s = 0; s < step; s++)
                newPixels[s + y * step + x * h * step] = pixels[s + x * step + y * w * step];

            Marshal.FreeHGlobal(data.Item1);
            return newData;
        }

        private static unsafe void FlipVertInPlace((IntPtr, int Length) data, int w, int h, int step)
        {
            byte* pixels = (byte*) data.Item1;
            for (var y = 0; y < h / 2; y++)
            {
                for (var x = 0; x < w * step; x++)
                {
                    var t = pixels[x + y * w * step];
                    pixels[x + y * w * step] = pixels[x + (h - y - 1) * w * step];
                    pixels[x + (h - y - 1) * w * step] = t;
                }
            }
        }

        private static unsafe void FlipHorInPlace((IntPtr, int Length) data, int w, int h, int step)
        {
            byte* pixels = (byte*) data.Item1;
            for (var x = 0; x < w / 2; x++)
            {
                for (var y = 0; y < h; y++)
                {
                    for (var s = 0; s < step; s++)
                    {
                        var t = pixels[s + x * step + y * w * step];
                        pixels[s + x * step + y * w * step] = pixels[s + (w - x - 1) * step + y * w * step];
                        pixels[s + (w - x - 1) * step + y * w * step] = t;
                    }
                }
            }
        }


        private static async Task EncodeAndSavePicture(string filePath, (IntPtr, int Length) pixelsPair, int w, int h,
            bool hasAlpha, int rotationQuarters, Metadata metadata)
        {
            if (pixelsPair.Item1 == IntPtr.Zero) return;
            
            // yield to background thread
            await Task.Delay(1).ConfigureAwait(false);
            
            if (Thread.CurrentThread == ourMainThread)
                MelonLogger.Error("Image encode is executed on main thread - it's a bug!");

            var step = hasAlpha ? 4 : 3;

            unsafe
            {
                // swap colors [a]rgb -> bgr[a]
                byte* pixels = (byte*) pixelsPair.Item1;
                for (int i = 0; i < pixelsPair.Length; i += step)
                {
                    var t = pixels[i];
                    pixels[i] = pixels[i + step - 1];
                    pixels[i + step - 1] = t;
                    if (step != 4) continue;

                    t = pixels[i + 1];
                    pixels[i + 1] = pixels[i + step - 2];
                    pixels[i + step - 2] = t;
                }
            }

            if (rotationQuarters == 1) //90deg cw
            {
                pixelsPair = TransposeAndDestroyOriginal(pixelsPair, w, h, step);
                var t = w;
                w = h;
                h = t;
            }
            else if (rotationQuarters == 2) //180deg cw
            {
                FlipHorInPlace(pixelsPair, w, h, step);
            }
            else if (rotationQuarters == 3) //270deg cw
            {
                FlipHorInPlace(pixelsPair, w, h, step);
                FlipVertInPlace(pixelsPair, w, h, step);
                pixelsPair = TransposeAndDestroyOriginal(pixelsPair, w, h, step);
                var t = w; w = h; h = t;
            }
            else
            {
                FlipVertInPlace(pixelsPair, w, h, step);
            }


            var pixelFormat = hasAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb;
            using var bitmap = new Bitmap(w, h, pixelFormat);
            var bitmapData = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, pixelFormat);
            unsafe
            {
                Buffer.MemoryCopy((void*) pixelsPair.Item1, (void*) bitmapData.Scan0, pixelsPair.Length, pixelsPair.Length);
            }

            bitmap.UnlockBits(bitmapData);
            Marshal.FreeHGlobal(pixelsPair.Item1);
            

            bitmap.Save(filePath, ImageFormat.Png);
            if (metadata != null)
            {
                using var pngStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
                var originalEndChunkBytes = new byte[12];
                pngStream.Position = pngStream.Length - 12;
                pngStream.Read(originalEndChunkBytes, 0, 12);
                pngStream.Position = pngStream.Length - 12;
                foreach (var kv in metadata.Get())
                {
                    var itxtChunk = PngUtils.ProducePngDescriptionTextChunk(kv.keyword, kv.value);
                    pngStream.Write(itxtChunk, 0, itxtChunk.Length);  
                }

                pngStream.Write(originalEndChunkBytes, 0, originalEndChunkBytes.Length);
            }

            await ourToMainThread.Yield();

            MelonLogger.Msg($"Image saved to {filePath}");

            // compatibility with log-reading tools
            UnityEngine.Debug.Log($"Took screenshot to: {filePath}");
            
            // yield to background thread for disposes
            await Task.Delay(1).ConfigureAwait(false);
        }

        private static Func<int, int, string> ourOurGetPathMethod;
        
        static string GetPath(int w, int h)
        {
            ourOurGetPathMethod ??= (Func<int, int, string>) Delegate.CreateDelegate(typeof(Func<int, int, string>),
                typeof(CameraUtil)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single(it =>
                        it.Name.StartsWith("Method_Private_Static_String_Int32_Int32_") && XrefScanner.XrefScan(it)
                            .Any(jt => jt.Type == XrefType.Global &&
                                       "yyyy-MM-dd_HH-mm-ss.fff" == jt.ReadAsObject()?.ToString())));

            return ourOurGetPathMethod(w, h);
        }
    }
}