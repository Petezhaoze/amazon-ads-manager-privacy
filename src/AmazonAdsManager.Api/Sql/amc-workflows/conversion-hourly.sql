-- AMC attributed conversions bucketed by AD-SERVING hour (traffic-time attribution).
-- Verified by AMC Agent on amcjk0ydh5o.
--
-- IMPORTANT SEMANTIC: this query intentionally uses `amazon_attributed_events_by_traffic_time`,
-- NOT `amazon_attributed_events_by_conversion_time`. For dayparting recommendations on bid
-- scheduling, we need to know which HOUR THE AD WAS SERVED that drove each conversion -- not
-- the hour the purchase happened. AMC docs: "Sponsored Products and Sponsored Display use
-- amazon_attributed_events_by_traffic_time and Sponsored Brands uses
-- amazon_attributed_events_by_conversion_time." This also makes the HourlyScorecard join
-- coherent: traffic-hourly and this conversion query both share the same `traffic_event_hour`
-- time axis, so (Date, Hour) buckets line up correctly on both sides.
--
-- We KEEP the output column aliases as `conversion_date` and `conversion_hour` so the existing
-- ingestion + DB schema (dbo.AmcConversionsHourly.ConversionDate/Hour) stay compatible without
-- a migration. Semantically those columns now hold the ad-serving date/hour of the attributed
-- conversion, not the purchase date/hour.
--
-- Other notes:
--   - No trailing semicolon.
--   - traffic_event_hour is a direct integer column (advertiser timezone).
--   - total_product_sales and new_to_brand_total_product_sales are in local currency (NO /1e8).
--   - All selected columns are LOW or NONE threshold per AMC Agent.

SELECT
  CAST(traffic_event_dt AS DATE) AS conversion_date,
  traffic_event_hour AS conversion_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  tracked_asin,
  conversion_event_type,
  SUM(purchases) AS purchases,
  SUM(units_sold) AS units_sold,
  SUM(total_product_sales) AS sales,
  SUM(new_to_brand_purchases) AS new_to_brand_purchases,
  SUM(new_to_brand_total_product_sales) AS new_to_brand_sales
FROM amazon_attributed_events_by_traffic_time
GROUP BY
  CAST(traffic_event_dt AS DATE),
  traffic_event_hour,
  campaign_id_string,
  campaign,
  ad_product_type,
  tracked_asin,
  conversion_event_type
