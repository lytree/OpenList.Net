import { defineComponent, ref, type PropType } from "vue"
import { ElEmpty, ElIcon, ElTag } from "element-plus"
import { Star, StarFilled, VideoCamera } from "@element-plus/icons-vue"
import { MediaItemStatus, mediaImageLink, type MediaItem } from "~/api/media"
import { t } from "~/i18n"

/**
 * 海报网格（媒体域内复用）。
 *
 * 从 `pages/media/index.tsx` 里抽出来的原因是 M4 第三片一次性多出四个需要
 * 「一屏海报」的页面（人物作品、片商作品、系列作品、播放列表详情）。
 * 复制四份海报卡片意味着四份「加载失败换占位块」的逻辑要同步维护。
 *
 * 与首页 `GridView` 的区别：那边是**文件**网格（图标 + 大小 + 时间 + 右键菜单），
 * 这边是**条目**网格（海报 + 评分 + 收藏 + 状态徽标），数据形状完全不同。
 */
export default defineComponent({
  name: "MediaGrid",
  props: {
    items: { type: Array as PropType<MediaItem[]>, default: () => [] },
    /** 已收藏的条目 id（父组件持有，切换后回传刷新）。 */
    favoriteIds: { type: Array as PropType<number[]>, default: () => [] },
    emptyText: { type: String, default: "" },
    /** 是否显示右上角收藏星标（播放列表等场景可以关掉）。 */
    showFavorite: { type: Boolean, default: true },
    /**
     * 排版：`grid` 是自适应海报墙（媒体库/收藏/作品列表），
     * `strip` 是单行横滑条（媒体首页的「继续观看」）。
     * 卡片本身只有一份实现，只有容器与卡片宽度不同。
     */
    layout: { type: String as PropType<"grid" | "strip">, default: "grid" },
  },
  emits: {
    open: (_item: MediaItem) => true,
    favorite: (_item: MediaItem) => true,
  },
  setup(props, { emit }) {
    /** 海报加载失败的条目：换成占位块而不是裂图（无 sidecar 的条目很常见）。 */
    const broken = ref<Set<number>>(new Set())

    const markBroken = (id: number) => {
      const next = new Set(broken.value)
      next.add(id)
      broken.value = next
    }

    const statusTag = (item: MediaItem) => {
      if (item.status === MediaItemStatus.Pending) return t("media.status_pending")
      if (item.status === MediaItemStatus.Unmatched) return t("media.status_unmatched")
      return ""
    }

    return () => {
      if (props.items.length === 0) {
        return <ElEmpty description={props.emptyText || t("media.no_items")} />
      }
      const favoriteSet = new Set(props.favoriteIds)
      const strip = props.layout === "strip"
      return (
        <div class={strip ? "ol-media-strip" : "ol-media-grid"}>
          {props.items.map((item) => {
            const fav = favoriteSet.has(item.id)
            return (
              <div
                key={item.id}
                class={strip ? "ol-media-card is-strip" : "ol-media-card"}
                onClick={() => emit("open", item)}
              >
                <div class="ol-media-poster">
                  {item.poster_path && !broken.value.has(item.id) ? (
                    <img
                      src={mediaImageLink(item.id)}
                      loading="lazy"
                      alt={item.title}
                      onError={() => markBroken(item.id)}
                    />
                  ) : (
                    <div class="ol-media-poster-fallback">
                      <ElIcon>
                        <VideoCamera />
                      </ElIcon>
                      <span>{t("media.no_poster")}</span>
                    </div>
                  )}
                  {item.rating ? <span class="ol-media-rating">★ {item.rating.toFixed(1)}</span> : null}
                  {props.showFavorite ? (
                    <span
                      class={fav ? "ol-media-fav is-on" : "ol-media-fav"}
                      title={fav ? t("media.unfavorite") : t("media.favorite")}
                      onClick={(event: MouseEvent) => {
                        event.stopPropagation()
                        emit("favorite", item)
                      }}
                    >
                      <ElIcon>{fav ? <StarFilled /> : <Star />}</ElIcon>
                    </span>
                  ) : null}
                </div>
                <div class="ol-media-card-title" title={item.title}>
                  {item.title}
                </div>
                <div class="ol-media-card-sub">
                  {item.year ? <span>{item.year}</span> : null}
                  {item.code ? <span class="ol-media-code">{item.code}</span> : null}
                  {statusTag(item) ? (
                    <ElTag size="small" type="warning">
                      {statusTag(item)}
                    </ElTag>
                  ) : null}
                </div>
              </div>
            )
          })}
        </div>
      )
    }
  },
})
