import { useCallback } from 'react'
import { useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { listCaptures } from '../api/captures'
import type { CaptureFilters, CapturedRequestSummary, TrafficCaptureEvent } from '../types/api'
import type { CaptureListResponse } from '../types/api'

const PAGE_SIZE = 50
const LIVE_CAP = 500

/**
 * Whether a live-streamed capture belongs in a list loaded with `filters`. The hub sends every
 * capture to every client, so the list has to apply its own filters to live events, mirroring
 * the server's `CaptureRepository.ApplyFilter`: host and client IP are substring matches, method
 * and status are exact. A body search (`q`) can't be checked against a live event, which carries
 * no bodies, so live events are held back while one is active and show up on the next refetch.
 */
export function captureMatchesFilters(event: TrafficCaptureEvent, filters: CaptureFilters): boolean {
  if (filters.q) return false
  if (filters.deviceId && event.deviceId !== filters.deviceId) return false
  if (filters.host && !event.host.includes(filters.host)) return false
  if (filters.method && event.method !== filters.method.toUpperCase()) return false
  if (filters.statusCode != null && event.statusCode !== filters.statusCode) return false
  if (filters.clientIp && !event.clientIp.includes(filters.clientIp)) return false
  const ts = Date.parse(event.timestamp)
  if (filters.from && ts < Date.parse(filters.from)) return false
  if (filters.to && ts > Date.parse(filters.to)) return false
  return true
}

export function useCaptures(filters: CaptureFilters) {
  const queryClient = useQueryClient()
  const filtersKey = JSON.stringify(filters)
  const queryKey = ['captures', filtersKey]

  const query = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) =>
      listCaptures({ ...filters, page: pageParam as number, pageSize: PAGE_SIZE }),
    initialPageParam: 1,
    getNextPageParam: (lastPage: CaptureListResponse) =>
      lastPage.page < lastPage.pages ? lastPage.page + 1 : undefined,
    staleTime: 0,
  })

  const captures = query.data?.pages.flatMap((p) => p.items) ?? []
  const firstPage = query.data?.pages[0]
  const lastPage = query.data?.pages[query.data.pages.length - 1]
  const total = firstPage?.total ?? 0

  const loadMore = useCallback(() => {
    if (!query.isFetchingNextPage && query.hasNextPage) {
      void query.fetchNextPage()
    }
  }, [query])

  const prependCapture = useCallback(
    (event: TrafficCaptureEvent) => {
      if (!captureMatchesFilters(event, filters)) return
      const partial: CapturedRequestSummary = {
        ...event,
        requestHeaders: '',
        responseHeaders: '',
        tlsCipherSuite: '',
        isModified: false,
        notes: '',
        device: undefined,
      }
      queryClient.setQueryData(
        queryKey,
        (old: InfiniteData<CaptureListResponse> | undefined) => {
          if (!old || old.pages.length === 0) return old
          const liveItems = [partial, ...old.pages[0].items]
          return {
            ...old,
            pages: [
              {
                ...old.pages[0],
                items: liveItems.length > LIVE_CAP ? liveItems.slice(0, LIVE_CAP) : liveItems,
                total: old.pages[0].total + 1,
              },
              ...old.pages.slice(1),
            ],
          }
        },
      )
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [queryClient, filtersKey],
  )

  return {
    captures,
    total,
    page: lastPage?.page ?? 1,
    pages: lastPage?.pages ?? 1,
    loading: query.isLoading,
    loadingMore: query.isFetchingNextPage,
    error: query.error instanceof Error ? query.error.message : null,
    loadMore,
    prependCapture,
    hasMore: query.hasNextPage ?? false,
  }
}
