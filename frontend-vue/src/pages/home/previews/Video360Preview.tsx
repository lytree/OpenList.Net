import { defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon } from "element-plus"
import { Refresh, VideoPlay } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import type * as ThreeTypes from "three"
import type { OrbitControls as OrbitControlsType } from "three/examples/jsm/controls/OrbitControls.js"
import type { PreviewChildProps } from "./types"

/**
 * 360° / VR 全景视频预览（等距柱状投影 / equirectangular）。
 *
 * 实现：three.js 在相机位置放一个**内翻**球体（`scale.x = -1` 让贴图朝内），
 * 把 `<video>` 当纹理贴上去，拖动 = 转相机（OrbitControls），滚轮 = 改 fov 缩放。
 *
 * 为什么不用 `@egjs/view360`：它内部也依赖 three，但封装层把「视频元素生命周期」
 * 和「渲染循环」都藏起来了，出错时只能看到黑屏；直接写 three 大约 200 行，
 * 且能自己处理「WebGL 不可用 → 退回普通播放器」这类降级。
 *
 * three.js 走动态 import：只有真的打开全景视频才下载这个 chunk。
 */

export default defineComponent({
  name: "Video360Preview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const host = ref<HTMLElement | null>(null)
    const video = ref<HTMLVideoElement | null>(null)
    /** three 初始化失败（WebGL 不可用 / chunk 加载失败）时退回普通 `<video>`。 */
    const fallback = ref("")
    const needsTap = ref(false)
    const loading = ref(true)

    let renderer: ThreeTypes.WebGLRenderer | null = null
    let controls: OrbitControlsType | null = null
    let scene: ThreeTypes.Scene | null = null
    let camera: ThreeTypes.PerspectiveCamera | null = null
    let texture: ThreeTypes.VideoTexture | null = null
    let raf = 0
    let resizeObserver: ResizeObserver | null = null

    /** 播放被浏览器策略拦下（无用户手势）时显示「点一下播放」遮罩。 */
    async function tryPlay() {
      const el = video.value
      if (!el) return
      try {
        await el.play()
        needsTap.value = false
      } catch {
        needsTap.value = true
      }
    }

    async function initThree() {
      const element = host.value
      const el = video.value
      if (!element || !el) return

      let THREE: typeof import("three")
      let OrbitControlsCtor: typeof import("three/examples/jsm/controls/OrbitControls.js").OrbitControls
      try {
        THREE = await import("three")
        ;({ OrbitControls: OrbitControlsCtor } = await import(
          "three/examples/jsm/controls/OrbitControls.js"
        ))
      } catch (e) {
        fallback.value = String((e as Error)?.message ?? e)
        return
      }

      try {
        const width = element.clientWidth || 640
        const height = element.clientHeight || 360

        const r = new THREE.WebGLRenderer({ antialias: true })
        r.setPixelRatio(Math.min(window.devicePixelRatio, 2))
        r.setSize(width, height)
        element.appendChild(r.domElement)

        const s = new THREE.Scene()
        // fov 75 接近人眼视野；滚轮缩放在 30–110 之间收口（见 resetView）
        const c = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000)
        c.position.set(0, 0, 0)

        const tex = new THREE.VideoTexture(el)
        tex.colorSpace = THREE.SRGBColorSpace
        // 球体 64×40 段足够（等距柱状图在极点会被拉伸，段数太低接缝更明显）
        const geo = new THREE.SphereGeometry(500, 64, 40)
        // side=BackSide 是**必须**的：相机在球心，能看到的只有球的内表面；
        // 默认 FrontSide 会把内表面全部剔除，结果是「canvas 有尺寸但全黑」。
        const mat = new THREE.MeshBasicMaterial({ map: tex, side: THREE.BackSide })
        const mesh = new THREE.Mesh(geo, mat)
        // 贴图朝内：反转 x 轴让水平方向不镜像（否则左右颠倒）
        mesh.scale.x = -1
        s.add(mesh)

        const orbit = new OrbitControlsCtor(c, r.domElement)
        // 阻尼让拖动有惯性；阻尼需要每帧 update()
        orbit.enableDamping = true
        orbit.target.set(0, 0, 0)

        scene = s
        camera = c
        texture = tex
        controls = orbit
        renderer = r

        const loop = () => {
          raf = requestAnimationFrame(loop)
          controls?.update()
          // VideoTexture 不会自己感知新帧，必须每帧置脏
          if (texture) texture.needsUpdate = true
          renderer?.render(s, c)
        }
        loop()

        resizeObserver = new ResizeObserver(() => {
          if (!renderer || !camera) return
          const w = element.clientWidth || 640
          const h = element.clientHeight || 360
          renderer.setSize(w, h)
          camera.aspect = w / h
          camera.updateProjectionMatrix()
        })
        resizeObserver.observe(element)

        loading.value = false
        await tryPlay()
      } catch (e) {
        // WebGL 上下文创建失败（无 GPU / 软件渲染被禁）时走降级，不让整页报错
        fallback.value = String((e as Error)?.message ?? e)
      }
    }

    onMounted(() => {
      // 先等元数据：VideoTexture 在 readyState 之前纹理尺寸是 0，会先黑一帧
      const el = video.value
      if (!el) return
      if (el.readyState >= 1) void initThree()
      else el.addEventListener("loadedmetadata", () => void initThree(), { once: true })
    })

    onBeforeUnmount(() => {
      cancelAnimationFrame(raf)
      resizeObserver?.disconnect()
      resizeObserver = null
      controls?.dispose()
      controls = null
      scene?.traverse((obj) => {
        const mesh = obj as ThreeTypes.Mesh
        mesh.geometry?.dispose()
        const material = mesh.material as ThreeTypes.MeshBasicMaterial | undefined
        material?.map?.dispose()
        material?.dispose()
      })
      scene = null
      texture = null
      camera = null
      if (renderer) {
        renderer.domElement.remove()
        renderer.dispose()
        renderer = null
      }
      const el = video.value
      if (el) {
        el.pause()
        // 清 src 让浏览器释放解码器（大文件不释放会一直占内存）
        el.removeAttribute("src")
        el.load()
      }
    })

    function resetView() {
      if (camera) {
        camera.fov = 75
        camera.updateProjectionMatrix()
      }
      controls?.target.set(0, 0, 0)
    }

    return () => (
      <div class="ol-video360">
        <div class="ol-video360-stage">
          <video
            ref={video}
            class="ol-video360-video"
            src={props.src}
            playsinline={true}
            controls={false}
            loop={false}
            preload="metadata"
            style={fallback.value ? "" : { display: "none" }}
          />
          {fallback.value ? (
            <div class="ol-video360-fallback">
              <ElAlert
                type="warning"
                showIcon={true}
                closable={false}
                title={t("home.preview.video360_fallback")}
                description={fallback.value}
              />
              <video class="ol-media-el" src={props.src} controls={true} playsinline={true} />
            </div>
          ) : (
            <div ref={host} class="ol-video360-host">
              {loading.value && <div class="ol-video360-loading">{t("global.loading")}</div>}
              {needsTap.value && (
                <div class="ol-video360-tap" onClick={() => void tryPlay()}>
                  <ElIcon size={40}>
                    <VideoPlay />
                  </ElIcon>
                  <span>{t("home.preview.click_to_play")}</span>
                </div>
              )}
            </div>
          )}
        </div>

        <div class="ol-preview-toolbar">
          <span class="ol-preview-hint">{t("home.preview.video360_hint")}</span>
          <div class="ol-preview-toolbar-right">
            <ElButton size="small" onClick={resetView}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Refresh />
              </ElIcon>
              {t("home.preview.video360_reset")}
            </ElButton>
          </div>
        </div>
      </div>
    )
  },
})
