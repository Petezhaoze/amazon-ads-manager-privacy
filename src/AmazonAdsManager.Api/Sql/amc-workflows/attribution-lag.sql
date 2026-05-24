-- AMC traffic-hour to conversion-hour lag behavior.
-- Verified by AMC Agent on amcjk0ydh5o for date range Apr 25 - May 24, 2026.
-- Notes:
--   - Date range is set by the workflow execution's timeWindowStart/End (no WHERE filter needed).
--   - JOIN key for sponsored_ads_traffic <-> amazon_attributed_events_by_conversion_time is
--     t.event_id = c.traffic_event_id (NOT request_tag, which is DSP-only).
--   - Both sides are deduplicated in CTEs first to avoid join inflation on the join key.
--   - DATE_DIFF is unsupported in AMC SQL; use SECONDS_BETWEEN(a, b) / 3600.0.
--   - event_hour and conversion_event_hour are direct integer columns (advertiser TZ).
--   - total_product_sales is in local currency; no /1e8 divisor.

WITH traffic AS (
  SELECT
    event_id,
    campaign_id_string,
    targeting,
    customer_search_term,
    CAST(event_dt AS DATE) AS traffic_date,
    event_hour AS traffic_hour,
    event_dt
  FROM sponsored_ads_traffic
  WHERE event_id IS NOT NULL
  GROUP BY
    event_id,
    campaign_id_string,
    targeting,
    customer_search_term,
    CAST(event_dt AS DATE),
    event_hour,
    event_dt
),
conversions AS (
  SELECT
    traffic_event_id,
    CAST(conversion_event_dt AS DATE) AS conversion_date,
    conversion_event_hour AS conversion_hour,
    conversion_event_dt,
    SUM(purchases) AS purchases,
    SUM(total_product_sales) AS sales
  FROM amazon_attributed_events_by_conversion_time
  WHERE traffic_event_id IS NOT NULL
  GROUP BY
    traffic_event_id,
    CAST(conversion_event_dt AS DATE),
    conversion_event_hour,
    conversion_event_dt
)
SELECT
  t.campaign_id_string AS campaign_id,
  t.targeting AS targeting_text,
  t.customer_search_term AS search_term,
  t.traffic_date,
  t.traffic_hour,
  c.conversion_date,
  c.conversion_hour,
  SECONDS_BETWEEN(t.event_dt, c.conversion_event_dt) / 3600.0 AS hours_to_conversion,
  SUM(c.purchases) AS purchases,
  SUM(c.sales) AS sales
FROM traffic t
JOIN conversions c
  ON t.event_id = c.traffic_event_id
GROUP BY
  t.campaign_id_string,
  t.targeting,
  t.customer_search_term,
  t.traffic_date,
  t.traffic_hour,
  c.conversion_date,
  c.conversion_hour,
  SECONDS_BETWEEN(t.event_dt, c.conversion_event_dt) / 3600.0
