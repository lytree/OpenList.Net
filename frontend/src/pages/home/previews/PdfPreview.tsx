import { defineComponent, type PropType } from "vue"
import type { PreviewChildProps } from "./types"

/**
 * PDF 预览。
 *
 * 直接交给浏览器内置 PDF 阅读器（iframe 指向 `/p` 代理地址）：
 * 桌面 Chrome/Edge/Firefox 与移动端都能内嵌，缩放/翻页/搜索由阅读器提供。
 * 不引入 pdf.js —— 它的 worker + 字体包体积（≈ 2 MB）与收益不成正比。
 */
export default defineComponent({
  name: "PdfPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    return () => (
      <iframe class="ol-frame-el" src={props.src} title={props.obj.name} />
    )
  },
})
