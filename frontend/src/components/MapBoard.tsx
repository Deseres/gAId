import { useEffect, useState, useMemo } from 'react';
import { Map, AdvancedMarker, useMap, useMapsLibrary } from '@vis.gl/react-google-maps';
import type { Place } from '../types';

const PREVIEW_COLORS = ['#ec4899', '#8b5cf6', '#f97316', '#14b8a6', '#eab308'];

function MapCameraController({ locations, isHalfScreen }: { locations: Place[], isHalfScreen: boolean }) {
  const map = useMap();
  
  useEffect(() => {
    if (!map || locations.length === 0) return;
    
    if (locations.length === 1) {
      map.panTo({ lat: locations[0].lat, lng: locations[0].lng });
      map.setZoom(15);
    } else {
      const bounds = new google.maps.LatLngBounds();
      locations.forEach(loc => bounds.extend({ lat: loc.lat, lng: loc.lng }));
      map.fitBounds(bounds, { 
        top: 50, 
        bottom: isHalfScreen ? window.innerHeight / 2 + 50 : 100, 
        left: 50, 
        right: 50 
      });
    }
  }, [map, locations, isHalfScreen]);
  
  return null;
}

function RouteLine({ locations }: { locations: Place[] }) {
  const map = useMap();
  const routesLibrary = useMapsLibrary('routes');
  const [directionsService, setDirectionsService] = useState<google.maps.DirectionsService>();
  const [directionsRenderer, setDirectionsRenderer] = useState<google.maps.DirectionsRenderer>();

  useEffect(() => {
    if (!routesLibrary || !map) return;
    
    const service = new routesLibrary.DirectionsService();
    const renderer = new routesLibrary.DirectionsRenderer({ 
      map, 
      suppressMarkers: true,
      polylineOptions: {
        strokeColor: '#2563eb',
        strokeWeight: 5,
      }
    });

    setDirectionsService(service);
    setDirectionsRenderer(renderer);

    return () => {
      renderer.setMap(null);
    };
  }, [routesLibrary, map]);

  useEffect(() => {
    if (!directionsService || !directionsRenderer || locations.length < 2) {
      if (directionsRenderer) directionsRenderer.setDirections(null);
      return;
    }

    const origin = locations[0];
    const destination = locations[locations.length - 1];
    const waypoints = locations.slice(1, -1).map(loc => ({
      location: { lat: loc.lat, lng: loc.lng },
      stopover: true
    }));

    directionsService.route({
      origin: { lat: origin.lat, lng: origin.lng },
      destination: { lat: destination.lat, lng: destination.lng },
      waypoints,
      optimizeWaypoints: true,
      travelMode: google.maps.TravelMode.WALKING,
    }).then(response => {
      directionsRenderer.setDirections(response);
    }).catch(() => {});
  }, [directionsService, directionsRenderer, locations]);

  return null;
}

function PreviewLine({ start, end, color }: { start: Place, end: Place, color: string }) {
  const map = useMap();
  const routesLibrary = useMapsLibrary('routes');
  const [directionsService, setDirectionsService] = useState<google.maps.DirectionsService>();
  const [directionsRenderer, setDirectionsRenderer] = useState<google.maps.DirectionsRenderer>();

  useEffect(() => {
    if (!routesLibrary || !map) return;
    
    const service = new routesLibrary.DirectionsService();
    const renderer = new routesLibrary.DirectionsRenderer({
      map,
      suppressMarkers: true,
      polylineOptions: {
        strokeOpacity: 0,
        icons: [{
          icon: {
            path: 'M 0,-1 0,1',
            strokeOpacity: 1,
            strokeWeight: 4,
            strokeColor: color,
            scale: 1
          },
          offset: '0',
          repeat: '20px'
        }]
      }
    });

    setDirectionsService(service);
    setDirectionsRenderer(renderer);

    return () => {
      renderer.setMap(null);
    };
  }, [routesLibrary, map, color]);

  useEffect(() => {
    if (!directionsService || !directionsRenderer) return;

    directionsService.route({
      origin: { lat: start.lat, lng: start.lng },
      destination: { lat: end.lat, lng: end.lng },
      travelMode: google.maps.TravelMode.WALKING,
    }).then(response => {
      directionsRenderer.setDirections(response);
    }).catch(() => {});
  }, [directionsService, directionsRenderer, start, end]);

  return null;
}

function PendingMarker({ loc, isActive, color, onClick }: { loc: Place, isActive: boolean, color: string, onClick: () => void }) {
  const [imgError, setImgError] = useState(false);
  const firstPhoto = loc.photos?.[0];
  const showImg = firstPhoto?.url && !imgError;

  return (
    <AdvancedMarker
      position={{ lat: loc.lat, lng: loc.lng }}
      zIndex={isActive ? 100 : 1}
      onClick={onClick}
    >
      <div 
        className={`w-11 h-11 rounded-full shadow-xl flex items-center justify-center overflow-hidden transition-all duration-300 cursor-pointer ${
          isActive
            ? 'scale-125 opacity-100 border-4'
            : 'bg-white border-2 border-gray-300 opacity-90 scale-90'
        }`}
        style={isActive ? { borderColor: color, backgroundColor: color } : {}}
      >
        {showImg ? (
          <img
            src={firstPhoto.url}
            alt={loc.name}
            className="w-full h-full object-cover pointer-events-none"
            onError={() => setImgError(true)}
          />
        ) : (
          <span className={`font-bold text-sm pointer-events-none ${isActive ? 'text-white' : 'text-gray-900'}`}>?</span>
        )}
      </div>
    </AdvancedMarker>
  );
}

interface MapBoardProps {
  confirmedRoute: Place[];
  pendingLocations: Place[];
  activePreviewId: string | null;
  isHalfScreen: boolean;
  onPreviewClick: (placeId: string) => void;
  onRouteClick: (placeId: string) => void;
}

export default function MapBoard({ 
  confirmedRoute, 
  pendingLocations, 
  activePreviewId, 
  isHalfScreen,
  onPreviewClick,
  onRouteClick
}: MapBoardProps) {
  const cameraLocations = useMemo(() => [...confirmedRoute, ...pendingLocations], [confirmedRoute, pendingLocations]);
  
  const startLocation = confirmedRoute.length > 0 ? confirmedRoute[confirmedRoute.length - 1] : null;
  const activePreviewLocation = pendingLocations.find(loc => loc.google_place_id === activePreviewId);
  const activeIndex = pendingLocations.findIndex(loc => loc.google_place_id === activePreviewId);
  const activeColor = activeIndex >= 0 ? PREVIEW_COLORS[activeIndex % PREVIEW_COLORS.length] : '#3b82f6';

  return (
    <Map
      defaultCenter={{ lat: 50.0614, lng: 19.9383 }}
      defaultZoom={14}
      mapId="DEMO_MAP_ID"
      disableDefaultUI={true}
      gestureHandling="greedy"
    >
      <MapCameraController locations={cameraLocations} isHalfScreen={isHalfScreen} />
      
      {confirmedRoute.length > 1 && (
        <RouteLine locations={confirmedRoute} />
      )}

      {startLocation && activePreviewLocation && (
        <PreviewLine start={startLocation} end={activePreviewLocation} color={activeColor} />
      )}
      
      {confirmedRoute.map((loc, index) => (
        <AdvancedMarker 
          key={loc.google_place_id} 
          position={{ lat: loc.lat, lng: loc.lng }}
          onClick={() => onRouteClick(loc.google_place_id)}
        >
          <div className="bg-blue-600 text-white px-3 py-1.5 rounded-full font-bold shadow-lg border-2 border-white text-sm cursor-pointer hover:scale-110 transition-transform">
            {index + 1}
          </div>
        </AdvancedMarker>
      ))}

      {pendingLocations.map((loc, index) => (
        <PendingMarker 
          key={loc.google_place_id} 
          loc={loc} 
          isActive={loc.google_place_id === activePreviewId}
          color={PREVIEW_COLORS[index % PREVIEW_COLORS.length]}
          onClick={() => onPreviewClick(loc.google_place_id)}
        />
      ))}
    </Map>
  );
}