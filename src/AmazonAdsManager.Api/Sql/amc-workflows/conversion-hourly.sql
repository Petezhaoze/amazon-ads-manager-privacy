-- AMC conversion-time metrics by conversion hour.
-- Export columns are named to match /api/amc/import-results?resultType=conversion-hourly.
-- Adjust conversion event filters for your brand's purchase/conversion definitions.

SELECT
  CAST(conversion_event_dt AS DATE) AS conversion_date,
  EXTRACT(HOUR FROM conversion_event_dt) AS conversion_hour,
  'UTC' AS time_zone,
  campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  tracked_asin,
  conversion_event_type,
  SUM(purchases) AS purchases,
  SUM(units_sold) AS units_sold,
  SUM(total_product_sales) AS sales,
  SUM(new_to_brand_purchases) AS new_to_brand_purchases,
  SUM(new_to_brand_total_product_sales) AS new_to_brand_sales
FROM amazon_attributed_events_by_conversion_time
WHERE conversion_event_dt BETWEEN @start_date AND @end_date
GROUP BY
  CAST(conversion_event_dt AS DATE),
  EXTRACT(HOUR FROM conversion_event_dt),
  campaign_id,
  campaign,
  ad_product_type,
  tracked_asin,
  conversion_event_type;
