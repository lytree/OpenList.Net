import { defineComponent, ref, watch, type PropType } from "vue"
import { ElIcon } from "element-plus"
import type { Obj } from "~/api/types"
import { kindColor, kindIcon } from "~/pages/home/kind"

/**
 * 列表行的缩略图槽位：有 `thumb` 就画 40px 小图，没有（或加载失败）就退回文件类型图标。
 *
 * 为什么单独成一个组件：桌面表格与手机卡片行都要同一个槽位，而「加载失败降级」
 * 是有状态的（每条独立），写在父组件的渲染函数里会变成「一行失败整表降级」。
 *
 * 与网格（`GridView`）的差异：网格失败后还会再退一次「原图直链」，因为那里一张图
 * 占满整格、退回图标损失太大；列表槽位只有 40px，退原图等于为了一个缩略框下一次全尺寸
 * 下载，不划算 —— 所以列表只做「缩略图 → 图标」一级降级。
 *
 * 后端只对图片/音频下发 `thumb`（`ThumbKinds.IsListSafe`），视频与 PDF 在列表里
 * 恒为空、直接走图标：一屏 20 个视频等于 20 次远端抽帧，这条约定在列表侧同样成立。
 */
export default defineComponent({
  name: "FileThumb",
  props: {
    obj: { type: Object as PropType<Obj>, required: true },
    /** 槽位边长（像素，正方形）。 */
    size: { type: Number, default: 40 },
    /** 图标态的图标尺寸。 */
    iconSize: { type: Number, default: 20 },
  },
  setup(props) {
    const failed = ref(false)
    // 复用同一个组件实例切换对象时（虚拟滚动 / 同名重载）要把失败态清掉，
    // 否则一次坏图会让这条记录永远显示图标。
    watch(
      () => [props.obj.name, props.obj.thumb] as const,
      () => {
        failed.value = false
      },
    )

    return () => {
      const Icon = kindIcon(props.obj)
      const box = { width: `${props.size}px`, height: `${props.size}px` }
      const src = props.obj.is_dir ? "" : props.obj.thumb

      if (src && !failed.value) {
        return (
          <span class="ol-thumb" style={box}>
            <img
              src={src}
              alt=""
              loading="lazy"
              decoding="async"
              onError={() => {
                failed.value = true
              }}
            />
          </span>
        )
      }

      return (
        <span class="ol-thumb is-icon" style={box}>
          <ElIcon size={props.iconSize} color={kindColor(props.obj)}>
            <Icon />
          </ElIcon>
        </span>
      )
    }
  },
})
