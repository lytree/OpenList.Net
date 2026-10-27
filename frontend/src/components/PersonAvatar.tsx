import { defineComponent, ref, watch, type PropType } from "vue"
import { mediaPersonImageLink } from "~/api/media"
import { nameInitial } from "~/utils/mediaCatalog"

/**
 * 人物头像。
 *
 * 两条硬约束，都是踩过的：
 * 1. **有 `profilePath` 才发请求**。本地 NFO 里没有 `<thumb>` 的人物非常多，
 *    盲请求只会换回一堆 404，而 Chromium 会把资源 404 记进控制台 ——
 *    等于自己制造「有报错」的假象（零错误断言当场变红）。
 * 2. **失败只降级、不重试**。图被删、路径失效时立刻退回姓名首字；
 *    `failed` 一旦置位就不再请求，翻页复用组件实例时按 `personId` 重置。
 *
 * 尺寸沿用 `.ol-media-avatar`（列表 56px / 详情 `is-lg` 84px）。
 */
export default defineComponent({
  name: "PersonAvatar",
  props: {
    personId: { type: Number, required: true },
    name: { type: String, default: "" },
    /** 后端 `profile_path`：外链 URL 或 VFS 路径；空表示没有头像。 */
    profilePath: { type: String as PropType<string | null | undefined>, default: null },
    large: { type: Boolean, default: false },
  },
  setup(props) {
    const failed = ref(false)
    watch(
      () => props.personId,
      () => {
        failed.value = false
      },
    )

    return () => {
      const cls = props.large ? "ol-media-avatar is-lg" : "ol-media-avatar"
      if (props.profilePath && !failed.value) {
        return (
          <div class={cls}>
            <img
              class="ol-person-avatar-img"
              src={mediaPersonImageLink(props.personId)}
              alt={props.name}
              loading="lazy"
              onError={() => {
                failed.value = true
              }}
            />
          </div>
        )
      }
      return <div class={cls}>{nameInitial(props.name)}</div>
    }
  },
})
