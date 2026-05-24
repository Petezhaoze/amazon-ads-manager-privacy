-- AMC Sponsored Ads traffic by traffic hour.
-- Verified by AMC Agent on amcjk0ydh5o.
-- Notes:
--   - No trailing semicolon. AMC treats `;` as a statement terminator; a stray `;` causes a
--     SUCCEEDED execution with header-only CSV (confirmed root cause of our zero-rows incident).
--     CleanWorkflowSql strips it as a safety net, but leave it out of source too.
--   - event_hour is a direct integer column (advertiser timezone); use it instead of EXTRACT.
--   - spend is in micro-microcents; divide by 1e8 to get local currency.
--   - All columns selected are LOW or NONE threshold (verified by AMC Agent against the
--     sponsored_ads_traffic schema). customer_search_term is HIGH (100+ users per row), so many
--     rows will land in a NULL bucket for that column at hourly grain. Expected; not a bug.

SELECT
  CAST(event_dt AS DATE) AS traffic_date,
  event_hour AS traffic_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  targeting AS targeting_text,
  match_type,
  customer_search_term,
  SUM(impressions) AS impressions,
  SUM(clicks) AS clicks,
  SUM(spend) / 100000000.0 AS spend
FROM sponsored_ads_traffic
GROUP BY
  CAST(event_dt AS DATE),
  event_hour,
  campaign_id_string,
  campaign,
  ad_product_type,
  targeting,
  match_type,
  customer_search_term
