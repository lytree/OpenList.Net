import { defineComponent, type PropType } from "vue"
import { ElCard, ElResult, ElTag } from "element-plus"
import { t } from "~/i18n"

/**
 * 里程碑占位页。
 *
 * 存在的意义不是「凑一个页面」，而是让侧边导航与路由表在 M0 就完整：
 * 导航项点击后落到明确的「属于哪个里程碑」而不是 404 或白屏。
 */
export default defineComponent({
  name: "MilestonePlaceholder",
  props: {
    title: { type: String, required: true },
    milestone: { type: String, required: true },
    points: { type: Array as PropType<string[]>, default: () => [] },
  },
  setup(props) {
    return () => (
      <ElCard shadow="never">
        <ElResult icon="info" title={props.title} subTitle={t("global.under_construction")}>
          {{
            extra: () => (
              <div style={{ display: "flex", flexDirection: "column", gap: "8px", alignItems: "center" }}>
                <ElTag type="warning" size="large">
                  {props.milestone}
                </ElTag>
                {props.points.length > 0 && (
                  <ul style={{ textAlign: "left", margin: 0, paddingLeft: "18px", color: "var(--ol-text-secondary)", fontSize: "13px" }}>
                    {props.points.map((p) => (
                      <li>{p}</li>
                    ))}
                  </ul>
                )}
              </div>
            ),
          }}
        </ElResult>
      </ElCard>
    )
  },
})
